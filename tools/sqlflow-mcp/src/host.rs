//! The server's entry point as a library: a host names its program, adds its modules, and runs.
//!
//! `sqlflow-mcp` itself is the host with no modules ([`McpHost::sqlflow`]). A product built on
//! SQLFlow ships a program of its own that composes the same server with the modules it adds, as it
//! composes the control plane and the CLI:
//!
//! ```ignore
//! #[tokio::main]
//! async fn main() -> anyhow::Result<()> {
//!     sqlflow_mcp::run(
//!         McpHost::new("probe-mcp", env!("CARGO_PKG_VERSION"))
//!             .registered_as("probe")
//!             .with_module(probe::module()),
//!     )
//!     .await
//! }
//! ```
//!
//! Everything about the server stays one program: the arguments (`--version`, `install`, `http`),
//! the transports, the sign-in, the environment variables and the tools. The host changes what the
//! program is called and what it adds, nothing else.

use std::sync::Arc;

use anyhow::Context;
use rmcp::transport::stdio;
use rmcp::ServiceExt;

use crate::config::{StateStore, DEFAULT_STATE_PREFIX};
use crate::control_plane::{ClientIdentity, ControlPlane};
use crate::docs::DocsIndex;
use crate::http_server;
use crate::links::{GuiLinks, LinkRule};
use crate::module::{is_module_name, McpContext, McpModule, ModuleSet};
use crate::modes::Mode;
use crate::server::SqlFlowMcp;

/// The name SQLFlow's own server runs under.
pub const SQLFLOW_SERVER_NAME: &str = "sqlflow-mcp";

/// The name a client registers SQLFlow's own server under (`claude mcp add sqlflow ...`).
pub const SQLFLOW_REGISTRATION: &str = "sqlflow";

/// A host of the MCP server: the program's name and version, the name a client registers it under,
/// where it keeps its sign-in, and the modules it adds.
pub struct McpHost {
    name: String,
    version: String,
    registration: String,
    state_prefix: String,
    modules: Vec<McpModule>,
    withheld: Vec<String>,
    introduction: Option<String>,
}

impl McpHost {
    /// SQLFlow's own server: no modules, and the names it has always had.
    pub fn sqlflow() -> Self {
        McpHost::new(SQLFLOW_SERVER_NAME, env!("CARGO_PKG_VERSION"))
    }

    /// A host whose program is `name` at `version` (the host crate's own, `env!("CARGO_PKG_VERSION")`).
    /// The name is what `--version` prints, what the server reports as its implementation, and how it
    /// names itself to the control plane (the user agent, and the client id of its device sign-in).
    pub fn new(name: impl Into<String>, version: impl Into<String>) -> Self {
        McpHost {
            name: name.into(),
            version: version.into(),
            registration: SQLFLOW_REGISTRATION.to_string(),
            state_prefix: DEFAULT_STATE_PREFIX.to_string(),
            modules: Vec::new(),
            withheld: Vec::new(),
            introduction: None,
        }
    }

    /// How the server introduces itself at the top of the instructions a client reads at `initialize`, in
    /// place of "SQLFlow MCP server.". A product built on SQLFlow says there what it is and how it relates to
    /// SQLFlow, so a model reads everything after it, SQLFlow's tools included, as this product's. Blank text
    /// is refused at start.
    pub fn introduced_as(mut self, text: impl Into<String>) -> Self {
        self.introduction = Some(text.into());
        self
    }

    /// The name `install` registers the server under in a client's configuration. SQLFlow's by
    /// default, so a host that replaces `sqlflow-mcp` on a machine keeps the registration its users have.
    pub fn registered_as(mut self, registration: impl Into<String>) -> Self {
        self.registration = registration.into();
        self
    }

    /// The prefix of the two files under `~/.sqlflow/` the server keeps its control-plane URL and its
    /// token in. A host that can be installed beside `sqlflow-mcp`, signed in to a control plane of
    /// its own, takes a prefix of its own so neither reads the other's token.
    pub fn state_prefix(mut self, prefix: impl Into<String>) -> Self {
        self.state_prefix = prefix.into();
        self
    }

    /// Leaves tools of SQLFlow's own server out of this host's: each is neither listed nor callable, and the
    /// instructions say it is not offered. For a product that does not offer part of what SQLFlow's server does
    /// (one that answers from metadata alone leaves out the tools that read rows). A name that is not one of
    /// SQLFlow's tools stops the host at start, so a tool renamed upstream is never offered again by accident.
    pub fn without_tools<I, S>(mut self, names: I) -> Self
    where
        I: IntoIterator<Item = S>,
        S: Into<String>,
    {
        self.withheld.extend(names.into_iter().map(Into::into));
        self
    }

    /// Adds a module. Modules are composed in the order they are added.
    pub fn with_module(mut self, module: McpModule) -> Self {
        self.modules.push(module);
        self
    }

    pub fn name(&self) -> &str {
        &self.name
    }

    pub fn version(&self) -> &str {
        &self.version
    }

    /// Checks the host's own names. A name ends up in an HTTP header, a file name and a client's
    /// configuration, so it is one token: letters, digits, hyphens, dots and underscores.
    fn validate(&self) -> anyhow::Result<StateStore> {
        for (what, value) in [("name", &self.name), ("registration", &self.registration)] {
            let token = !value.is_empty()
                && value.len() <= 64
                && value
                    .chars()
                    .all(|c| c.is_ascii_alphanumeric() || matches!(c, '-' | '.' | '_'));
            anyhow::ensure!(
                token,
                "the MCP host's {what} '{value}' is not usable: letters, digits, hyphens, dots and \
                 underscores, at most 64 characters"
            );
        }
        anyhow::ensure!(
            !self.version.trim().is_empty() && !self.version.chars().any(char::is_whitespace),
            "the MCP host's version '{}' is not usable: one token, without whitespace",
            self.version
        );
        if let Some(text) = &self.introduction {
            anyhow::ensure!(
                !text.trim().is_empty(),
                "the MCP host's introduction is blank; leave introduced_as out to keep SQLFlow's"
            );
        }
        StateStore::new(&self.state_prefix).map_err(|e| anyhow::anyhow!("the MCP host's state prefix: {e}"))
    }

    /// How the host's program names itself to the control plane, and where it keeps its sign-in.
    pub fn identity(&self) -> anyhow::Result<ClientIdentity> {
        Ok(ClientIdentity::new(&self.name, &self.version, self.validate()?))
    }

    /// The server this host composes, over a control-plane client and a GUI base the caller supplies
    /// (the client is built with [`identity`](Self::identity)). [`run`] is this with both read from
    /// the environment; calling it directly is how a host's own tests drive its server.
    pub fn server(self, cp: Arc<ControlPlane>, gui_base: &str, http_mode: bool) -> anyhow::Result<SqlFlowMcp> {
        self.validate()?;
        Ok(McpHost::compose(self.modules, self.withheld, self.introduction, cp, gui_base, http_mode)?
            .into_server(http_mode))
    }

    /// Composes the host's modules into the parts of the server that own each contribution. The
    /// control plane and the transport are known by now, since a module's tools are built with them.
    fn compose(
        modules: Vec<McpModule>,
        withheld: Vec<String>,
        introduction: Option<String>,
        cp: Arc<ControlPlane>,
        gui_base: &str,
        http_mode: bool,
    ) -> anyhow::Result<Composition> {
        let mut docs = DocsIndex::load();
        let mut rules: Vec<LinkRule> = Vec::new();
        let mut builders = Vec::new();
        let mut instructions = Vec::new();
        let mut names: Vec<String> = Vec::new();

        for module in modules {
            let parts = module.into_parts();
            anyhow::ensure!(
                is_module_name(&parts.name),
                "'{}' is not an MCP module name: lowercase letters, digits and hyphens, starting with a \
                 letter, at most 64 characters",
                parts.name
            );
            anyhow::ensure!(
                !names.contains(&parts.name),
                "two MCP modules of this host are named '{}'; a module's name is its own",
                parts.name
            );

            docs.add(parts.docs)
                .map_err(|e| anyhow::anyhow!("MCP module '{}': {e}", parts.name))?;
            for census in &parts.census {
                sqlflow_lang::census::register(census).map_err(|e| {
                    anyhow::anyhow!("MCP module '{}' registers a key census that is not one: {e}", parts.name)
                })?;
            }
            rules.extend(parts.link_rules);
            if let Some(text) = parts.instructions {
                instructions.push(text);
            }
            if let Some(build) = parts.tools {
                builders.push((parts.name.clone(), build));
            }
            names.push(parts.name);
        }

        // The links carry every module's rules before any module's tools are built, so a tool of one
        // module links a row another module's rule recognises.
        let links = GuiLinks::new(gui_base).with_rules(rules);
        let context = McpContext::new(cp.clone(), links.clone(), http_mode);

        let own = SqlFlowMcp::tool_names();
        for name in &withheld {
            anyhow::ensure!(
                own.contains(name),
                "the MCP host leaves out '{name}', which is not a tool of SQLFlow's server; a tool that was renamed \
                 or removed has to be named as it is now"
            );
        }
        let mut set = ModuleSet::default();
        set.withhold(withheld);
        if let Some(text) = &introduction {
            set.introduce(text);
        }
        for (name, build) in builders {
            let tools = build(context.clone());
            set.add_tools(&name, tools, |tool| own.contains(tool))
                .map_err(|e| anyhow::anyhow!(e))?;
        }
        for text in &instructions {
            set.add_instructions(text);
        }

        Ok(Composition {
            docs: Arc::new(docs),
            cp,
            links,
            modules: Arc::new(set),
            module_names: names,
        })
    }
}

/// A host's modules composed: what every server instance is created from.
struct Composition {
    docs: Arc<DocsIndex>,
    cp: Arc<ControlPlane>,
    links: GuiLinks,
    modules: Arc<ModuleSet>,
    module_names: Vec<String>,
}

impl Composition {
    fn into_server(self, http_mode: bool) -> SqlFlowMcp {
        SqlFlowMcp::composed(self.docs, self.cp, self.links, self.modules, http_mode)
    }
}

/// Runs the server a host describes, reading the program's arguments: `--version`, `install
/// [client] [--mode data|estate]`, `http [--bind <addr>] [--allowed-hosts <h1,h2>]`, or, for stdio, nothing or
/// `--mode data|estate` (`SQLFLOW_MCP_MODE` when the flag is absent), which registers the server in one mode for an
/// editor (`crate::modes`).
///
/// Logging goes to stderr so it never corrupts the stdio protocol channel.
pub async fn run(host: McpHost) -> anyhow::Result<()> {
    let identity = host.identity()?;
    let args: Vec<String> = std::env::args().collect();
    match args.get(1).map(String::as_str) {
        Some("--version") | Some("-V") => {
            println!("{} {}", host.name, host.version);
            return Ok(());
        }
        Some("install") => {
            let (client, mode) = parse_install_options(&host.name, &args[2..])?;
            print_install(&host, client.as_deref(), mode);
            return Ok(());
        }
        _ => {}
    }

    tracing_subscriber::fmt()
        .with_writer(std::io::stderr)
        .with_env_filter(
            tracing_subscriber::EnvFilter::try_from_env("SQLFLOW_MCP_LOG")
                .unwrap_or_else(|_| tracing_subscriber::EnvFilter::new("info")),
        )
        .init();

    let http_mode = args.get(1).map(String::as_str) == Some("http");
    // Read before anything starts, so a mistyped mode stops the program with its usage rather than serving every tool.
    let registered = if http_mode {
        if std::env::var("SQLFLOW_MCP_MODE").is_ok_and(|value| !value.trim().is_empty()) {
            tracing::warn!("SQLFLOW_MCP_MODE is ignored over HTTP: each session names its mode on the MCP address");
        }
        None
    } else {
        parse_stdio_options(&host.name, &args[1..], std::env::var("SQLFLOW_MCP_MODE").ok())?
    };
    // Over HTTP the server holds no credential of its own: every call runs as the bearer on the inbound request.
    let cp = Arc::new(ControlPlane::from_env(&identity, !http_mode));
    let gui_base = std::env::var("SQLFLOW_GUI_URL").unwrap_or_default();
    let composed = McpHost::compose(host.modules, host.withheld, host.introduction, cp, &gui_base, http_mode)?;

    tracing::info!(
        "loaded {} reference pages for {}",
        composed.docs.len(),
        composed.docs.product()
    );
    if !composed.module_names.is_empty() {
        tracing::info!(
            "modules: {} ({} tools)",
            composed.module_names.join(", "),
            composed.modules.list().len()
        );
    }
    if !composed.modules.withheld().is_empty() {
        tracing::info!("not offered: {}", composed.modules.withheld().join(", "));
    }
    tracing::info!("control plane: {}", composed.cp.base_url());
    match composed.links.base() {
        "" => tracing::info!(
            "GUI links: root-relative (set SQLFLOW_GUI_URL to make them absolute for clients outside the GUI)"
        ),
        base => tracing::info!("GUI links: {base}"),
    }

    let composed_cp = composed.cp.clone();
    let server = composed.into_server(http_mode);
    let server = match registered {
        Some(mode) => {
            tracing::info!("registered in {} mode: its sessions list that mode's tools and stay in it", mode.name());
            server.registered_in(mode)?
        }
        None => server,
    };

    if http_mode {
        let opts = parse_http_options(&host.name, &args[2..])?;
        return http_server::serve(server, opts).await;
    }

    // Over stdio this process holds the user's session: keep it rolling on the GUI's schedule while the server runs.
    composed_cp.spawn_session_keeper();

    let service = server.serve(stdio()).await?;
    service.waiting().await?;
    Ok(())
}

/// Options for `<server>` over stdio: `--mode data` or `--mode estate` registers it in one mode for an editor, the
/// flag winning over `env_mode` (`SQLFLOW_MCP_MODE`). Neither, or a blank variable, leaves its sessions in no mode,
/// listing every tool. Any other argument is refused with the usage, so a typo never starts a server that ignores it.
fn parse_stdio_options(program: &str, args: &[String], env_mode: Option<String>) -> anyhow::Result<Option<Mode>> {
    let mut flag: Option<String> = None;
    let mut iter = args.iter();
    while let Some(arg) = iter.next() {
        match arg.as_str() {
            "--mode" => {
                anyhow::ensure!(flag.is_none(), "--mode is given twice for `{program}`; name one mode");
                flag = Some(iter.next().context("--mode requires a mode: --mode data or --mode estate")?.clone());
            }
            other => anyhow::bail!(
                "unknown argument '{other}' for `{program}`; expected nothing or --mode data|estate for stdio, \
                 http [--bind <addr>] [--allowed-hosts <h1,h2>], install [client] [--mode data|estate], or --version"
            ),
        }
    }
    let (name, from) = match (flag, env_mode.filter(|value| !value.trim().is_empty())) {
        (Some(name), _) => (name, "--mode"),
        (None, Some(name)) => (name, "SQLFLOW_MCP_MODE"),
        (None, None) => return Ok(None),
    };
    Mode::named(name.trim())
        .map(Some)
        .with_context(|| format!("{from} '{name}' is not a mode; expected data or estate"))
}

/// Options for `<server> install`: the client to print a registration for (`claude`, `codex`, `cursor`, `vscode`, or
/// none for the generic JSON) and the mode to register it in, if any.
fn parse_install_options(program: &str, args: &[String]) -> anyhow::Result<(Option<String>, Option<Mode>)> {
    let mut client: Option<String> = None;
    let mut mode: Option<Mode> = None;
    let mut iter = args.iter();
    while let Some(arg) = iter.next() {
        match arg.as_str() {
            "--mode" => {
                let name = iter.next().context("--mode requires a mode: --mode data or --mode estate")?;
                mode = Some(
                    Mode::named(name).with_context(|| format!("--mode '{name}' is not a mode; expected data or estate"))?,
                );
            }
            other if !other.starts_with('-') && client.is_none() => client = Some(other.to_string()),
            other => anyhow::bail!(
                "unknown argument '{other}' for `{program} install`; expected [claude|codex|cursor|vscode] [--mode data|estate]"
            ),
        }
    }
    Ok((client, mode))
}

/// Options for `<server> http`: flags win over their environment fallbacks
/// (`SQLFLOW_MCP_HTTP_BIND`, `SQLFLOW_MCP_HTTP_ALLOWED_HOSTS`), and the default bind
/// is loopback so a bare local start is never accidentally network-exposed.
fn parse_http_options(program: &str, args: &[String]) -> anyhow::Result<http_server::HttpServerOptions> {
    let mut bind: Option<String> = None;
    let mut allowed_hosts: Option<String> = None;
    let mut iter = args.iter();
    while let Some(arg) = iter.next() {
        match arg.as_str() {
            "--bind" => {
                bind = Some(
                    iter.next()
                        .context("--bind requires an address, e.g. --bind 0.0.0.0:8080")?
                        .clone(),
                );
            }
            "--allowed-hosts" => {
                allowed_hosts = Some(
                    iter.next()
                        .context("--allowed-hosts requires a comma-separated list, e.g. --allowed-hosts mcp.example.com")?
                        .clone(),
                );
            }
            other => anyhow::bail!(
                "unknown argument '{other}' for `{program} http`; expected --bind <addr> and/or --allowed-hosts <h1,h2>"
            ),
        }
    }
    let bind = bind
        .or_else(|| std::env::var("SQLFLOW_MCP_HTTP_BIND").ok())
        .unwrap_or_else(|| "127.0.0.1:8787".to_string());
    let bind: std::net::SocketAddr = bind
        .parse()
        .with_context(|| format!("invalid bind address '{bind}'; expected host:port, e.g. 0.0.0.0:8080"))?;
    let allowed_hosts = allowed_hosts
        .or_else(|| std::env::var("SQLFLOW_MCP_HTTP_ALLOWED_HOSTS").ok())
        .unwrap_or_default()
        .split(',')
        .map(str::trim)
        .filter(|h| !h.is_empty())
        .map(String::from)
        .collect();
    Ok(http_server::HttpServerOptions { bind, allowed_hosts })
}

fn print_install(host: &McpHost, client: Option<&str>, mode: Option<Mode>) {
    let exe = std::env::current_exe()
        .map(|p| p.display().to_string())
        .unwrap_or_else(|_| host.name.clone());
    match install_text(&host.registration, &exe, client, mode) {
        Ok(text) => println!("{text}"),
        Err(message) => eprintln!("{message}"),
    }
}

/// The ready-to-paste registration of the server for one MCP client. A registration in a mode is named after it
/// (`<registration>-data`), so an editor holds one per mode side by side and a chat turns on the one it needs.
fn install_text(registration: &str, exe: &str, client: Option<&str>, mode: Option<Mode>) -> Result<String, String> {
    let name = match mode {
        Some(mode) => format!("{registration}-{}", mode.name()),
        None => registration.to_string(),
    };
    match client {
        Some("claude") => Ok(match mode {
            Some(mode) => format!("claude mcp add {name} -- {exe} --mode {}", mode.name()),
            None => format!("claude mcp add {name} -- {exe}"),
        }),
        Some("codex") => Ok(match mode {
            Some(mode) => format!("[mcp_servers.{name}]\ncommand = \"{exe}\"\nargs = [\"--mode\", \"{}\"]", mode.name()),
            None => format!("[mcp_servers.{name}]\ncommand = \"{exe}\""),
        }),
        Some("cursor") | Some("vscode") | None => {
            let args = match mode {
                Some(mode) => format!("\n      \"args\": [\"--mode\", \"{}\"],", mode.name()),
                None => String::new(),
            };
            Ok(format!(
                "{{\n  \"mcpServers\": {{\n    \"{name}\": {{\n      \"command\": \"{}\",{args}\n      \"env\": {{ \"SQLFLOW_CONTROL_PLANE_URL\": \"http://localhost:8080\" }}\n    }}\n  }}\n}}",
                exe.replace('\\', "\\\\")
            ))
        }
        Some(other) => Err(format!("Unknown client '{other}'. Try: claude, cursor, codex, vscode.")),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::docs::{DocMeta, DocPage};
    use crate::module::McpContext;
    use rmcp::handler::server::wrapper::Parameters;
    use rmcp::{schemars, tool, tool_router};
    use serde::Deserialize;
    use serde_json::json;

    #[derive(Debug, Deserialize, schemars::JsonSchema)]
    struct ProbeInput {
        /// The probe to read.
        name: String,
    }

    #[derive(Clone)]
    struct ProbeTools {
        ctx: McpContext,
    }

    #[tool_router(router = router)]
    impl ProbeTools {
        #[tool(description = "Describe one probe of the probe module.")]
        async fn describe_probe(&self, Parameters(input): Parameters<ProbeInput>) -> String {
            format!("probe {} over http={}", input.name, self.ctx.is_http())
        }
    }

    /// A second service that takes one of SQLFlow's own tool names.
    #[derive(Clone)]
    struct ShadowTools;

    #[tool_router(router = router)]
    impl ShadowTools {
        #[tool(description = "A tool named like one of SQLFlow's.")]
        async fn list_repos(&self, Parameters(_): Parameters<ProbeInput>) -> String {
            String::new()
        }
    }

    fn control_plane() -> Arc<ControlPlane> {
        let identity = ClientIdentity::new("probe-mcp", "1.2.3", StateStore::new("probe-mcp-test").unwrap());
        Arc::new(ControlPlane::new(&identity, "http://127.0.0.1:9", None, true))
    }

    fn probe_page(id: &str) -> DocPage {
        DocPage {
            meta: DocMeta::page(id, "Probes", "concept", "probes.md", "What a probe is.", &["probe"]),
            body: "# Probes\n\nA probe is a probe.",
        }
    }

    fn probe_module() -> McpModule {
        McpModule::new("probe")
            .tools(|ctx| (ProbeTools { ctx }, ProbeTools::router()))
            .instructions("PROBES:\n- describe_probe reads one.")
            .docs([probe_page("probe-concept")])
            .link_rule(|links, row| {
                let id = row.get("probeId")?.as_str()?;
                let mut found = serde_json::Map::new();
                found.insert("page".into(), links.route(&format!("/probes/{id}")).into());
                Some(found)
            })
    }

    fn compose(modules: Vec<McpModule>) -> anyhow::Result<Composition> {
        McpHost::compose(modules, Vec::new(), None, control_plane(), "https://gui.example.com", true)
    }

    /// Why a set of modules was refused; fails when it composed.
    fn refusal(modules: Vec<McpModule>) -> String {
        match compose(modules) {
            Ok(_) => panic!("the modules composed, and were expected to be refused"),
            Err(e) => e.to_string(),
        }
    }

    #[test]
    fn a_module_adds_its_tools_instructions_pages_and_links() {
        let composed = compose(vec![probe_module()]).expect("the probe module composes");

        let tools: Vec<&str> = composed.modules.list().iter().map(|t| t.name.as_ref()).collect();
        assert_eq!(tools, ["describe_probe"]);
        assert!(composed.modules.owner_of("describe_probe").is_some());
        assert!(composed.modules.owner_of("list_repos").is_none(), "SQLFlow's tools stay the router's");
        assert_eq!(composed.modules.instructions(), "\n\nPROBES:\n- describe_probe reads one.");

        // The page is searched and fetched beside SQLFlow's own corpus, which is still whole.
        assert!(composed.docs.body("probe-concept").is_some());
        assert!(composed.docs.len() > 70);
        assert_eq!(composed.docs.search("probe", None, 5)[0].meta.id, "probe-concept");

        // The module's rule gives its rows a destination, on the host's GUI base.
        let mut payload = json!([{ "probeId": "p-1", "runId": "run-1" }]);
        composed.links.decorate(&mut payload);
        assert_eq!(payload[0]["links"]["page"], json!("https://gui.example.com/probes/p-1"));
        assert_eq!(payload[0]["links"]["run"], json!("https://gui.example.com/runs/run-1"));
        assert_eq!(composed.module_names, ["probe"]);
    }

    #[test]
    fn a_host_without_modules_is_sqlflow_as_it_was() {
        let composed = compose(Vec::new()).expect("no modules compose");
        assert!(composed.modules.list().is_empty());
        assert_eq!(composed.modules.instructions(), "");
        assert_eq!(composed.docs.len(), DocsIndex::load().len());
    }

    #[test]
    fn a_tool_named_like_one_of_sqlflows_is_refused_naming_the_module() {
        let shadow = McpModule::new("shadow").tools(|_| (ShadowTools, ShadowTools::router()));
        let refused = refusal(vec![shadow]);
        assert!(refused.contains("'shadow'"), "{refused}");
        assert!(refused.contains("'list_repos'"), "{refused}");
        assert!(refused.contains("SQLFlow's own server already"), "{refused}");
    }

    #[test]
    fn two_modules_may_not_add_one_tool() {
        let second = McpModule::new("probe-two").tools(|ctx| (ProbeTools { ctx }, ProbeTools::router()));
        let refused = refusal(vec![probe_module_without_docs(), second]);
        assert!(refused.contains("'probe-two'"), "{refused}");
        assert!(refused.contains("'describe_probe'"), "{refused}");
        assert!(refused.contains("another module"), "{refused}");
    }

    fn probe_module_without_docs() -> McpModule {
        McpModule::new("probe").tools(|ctx| (ProbeTools { ctx }, ProbeTools::router()))
    }

    #[test]
    fn a_duplicate_or_malformed_module_name_is_refused() {
        let twice = refusal(vec![McpModule::new("probe"), McpModule::new("probe")]);
        assert!(twice.contains("two MCP modules"), "{twice}");

        for bad in ["", "Probe", "1probe", "probe module", "probe_module"] {
            let refused = refusal(vec![McpModule::new(bad)]);
            assert!(refused.contains("is not an MCP module name"), "{bad}: {refused}");
        }
    }

    #[test]
    fn a_page_id_already_indexed_is_refused_naming_the_module() {
        // `cli-run` is one of SQLFlow's own pages.
        let clash = McpModule::new("probe").docs([probe_page("cli-run")]);
        let refused = refusal(vec![clash]);
        assert!(refused.contains("MCP module 'probe'"), "{refused}");
        assert!(refused.contains("'cli-run'"), "{refused}");
    }

    #[test]
    fn a_census_that_is_not_one_is_refused_naming_the_module() {
        let broken = McpModule::new("probe").census("{ \"keys\": \"not a list\" }");
        let refused = refusal(vec![broken]);
        assert!(refused.contains("MCP module 'probe'"), "{refused}");
        assert!(refused.contains("key census"), "{refused}");
    }

    #[test]
    fn a_module_census_reaches_the_flow_language_tools() {
        let census = r#"{
            "flowType": "probekind",
            "keys": [
                { "path": "flowType", "type": "string", "required": true, "description": "The kind." },
                { "path": "probe.target", "type": "string", "required": true, "description": "What is probed." }
            ]
        }"#;
        compose(vec![McpModule::new("probe").census(census)]).expect("the census registers");

        let known = sqlflow_lang::census::Census::for_flow_type(Some("probekind"));
        assert!(known.entries.iter().any(|e| e.path == "probe.target"));
        sqlflow_lang::census::unregister(&sqlflow_lang::census::CensusKind::FlowType("probekind".into()));
    }

    #[test]
    fn a_host_leaves_out_tools_of_sqlflows_by_name() {
        let withheld = vec!["run_query".to_string(), "prepare_query".to_string(), "run_query".to_string()];
        let composed = McpHost::compose(Vec::new(), withheld, None, control_plane(), "", true).expect("the host composes");
        // Named once each, in order, whatever order and however often the host named them.
        assert_eq!(composed.modules.withheld(), ["prepare_query", "run_query"]);
        let note = composed.modules.withheld_note();
        assert!(note.starts_with("\n\nNot offered by this server"), "{note}");
        assert!(note.contains("prepare_query, run_query"), "{note}");

        // A host that leaves nothing out says nothing about it.
        assert_eq!(compose(Vec::new()).expect("no modules compose").modules.withheld_note(), "");
    }

    #[test]
    fn leaving_out_a_tool_sqlflow_does_not_have_is_refused() {
        let refused = match McpHost::compose(Vec::new(), vec!["run_querry".to_string()], None, control_plane(), "", true) {
            Ok(_) => panic!("a name that is no tool was accepted"),
            Err(e) => e.to_string(),
        };
        assert!(refused.contains("'run_querry'"), "{refused}");
        assert!(refused.contains("not a tool of SQLFlow's server"), "{refused}");
    }

    #[test]
    fn a_host_name_that_is_not_a_token_is_refused() {
        for bad in ["", "probe mcp", "probe/mcp", "probe\"mcp"] {
            let refused = McpHost::new(bad, "1.0.0").validate().expect_err(bad).to_string();
            assert!(refused.contains("is not usable"), "{bad}: {refused}");
        }
        assert!(McpHost::new("probe-mcp", "1.0.0").registered_as("pro be").validate().is_err());
        assert!(McpHost::new("probe-mcp", " ").validate().is_err());
        assert!(McpHost::new("probe-mcp", "1.0.0").state_prefix("../x").validate().is_err());
        assert_eq!(McpHost::sqlflow().validate().unwrap(), StateStore::default());
    }

    #[test]
    fn install_registers_the_host_under_its_registration() {
        assert_eq!(
            install_text("probe", "/opt/probe-mcp", Some("claude"), None).unwrap(),
            "claude mcp add probe -- /opt/probe-mcp"
        );
        assert_eq!(
            install_text("probe", "/opt/probe-mcp", Some("codex"), None).unwrap(),
            "[mcp_servers.probe]\ncommand = \"/opt/probe-mcp\""
        );
        let json = install_text("probe", "C:\\tools\\probe-mcp.exe", None, None).unwrap();
        assert!(json.contains("\"probe\": {"), "{json}");
        assert!(json.contains("C:\\\\tools\\\\probe-mcp.exe"), "{json}");
        assert!(!json.contains("\"args\""), "{json}");
        assert!(install_text("probe", "x", Some("emacs"), None).unwrap_err().contains("Unknown client 'emacs'"));

        // SQLFlow's own registration is the one its users already have.
        assert_eq!(
            install_text(SQLFLOW_REGISTRATION, "sqlflow-mcp", Some("claude"), None).unwrap(),
            "claude mcp add sqlflow -- sqlflow-mcp"
        );
    }

    /// A registration in a mode is named after the mode and passes it, so an editor holds one per mode side by side.
    #[test]
    fn install_in_a_mode_names_the_registration_after_it_and_passes_the_mode() {
        assert_eq!(
            install_text("probe", "/opt/probe-mcp", Some("claude"), Some(Mode::Data)).unwrap(),
            "claude mcp add probe-data -- /opt/probe-mcp --mode data"
        );
        assert_eq!(
            install_text("probe", "/opt/probe-mcp", Some("codex"), Some(Mode::Estate)).unwrap(),
            "[mcp_servers.probe-estate]\ncommand = \"/opt/probe-mcp\"\nargs = [\"--mode\", \"estate\"]"
        );
        let json = install_text("probe", "/opt/probe-mcp", Some("vscode"), Some(Mode::Data)).unwrap();
        let parsed: serde_json::Value = serde_json::from_str(&json).expect("the registration is JSON");
        assert_eq!(parsed["mcpServers"]["probe-data"]["args"], json!(["--mode", "data"]), "{json}");
        assert_eq!(parsed["mcpServers"]["probe-data"]["command"], json!("/opt/probe-mcp"), "{json}");
    }

    fn strings(args: &[&str]) -> Vec<String> {
        args.iter().map(|arg| arg.to_string()).collect()
    }

    #[test]
    fn a_stdio_server_is_registered_in_the_mode_its_flag_or_variable_names() {
        assert_eq!(parse_stdio_options("probe-mcp", &[], None).unwrap(), None);
        assert_eq!(parse_stdio_options("probe-mcp", &[], Some("  ".into())).unwrap(), None);
        assert_eq!(parse_stdio_options("probe-mcp", &strings(&["--mode", "data"]), None).unwrap(), Some(Mode::Data));
        assert_eq!(parse_stdio_options("probe-mcp", &[], Some("estate".into())).unwrap(), Some(Mode::Estate));
        // The flag wins over the variable.
        assert_eq!(
            parse_stdio_options("probe-mcp", &strings(&["--mode", "estate"]), Some("data".into())).unwrap(),
            Some(Mode::Estate)
        );
    }

    #[test]
    fn a_stdio_server_refuses_what_it_cannot_read_naming_where_it_came_from() {
        let typo = parse_stdio_options("probe-mcp", &strings(&["--mode", "datum"]), None).unwrap_err().to_string();
        assert!(typo.contains("--mode 'datum'") && typo.contains("data or estate"), "{typo}");
        let variable = parse_stdio_options("probe-mcp", &[], Some("Data".into())).unwrap_err().to_string();
        assert!(variable.contains("SQLFLOW_MCP_MODE 'Data'"), "{variable}");
        let missing = parse_stdio_options("probe-mcp", &strings(&["--mode"]), None).unwrap_err().to_string();
        assert!(missing.contains("--mode requires a mode"), "{missing}");
        let twice = parse_stdio_options("probe-mcp", &strings(&["--mode", "data", "--mode", "estate"]), None)
            .unwrap_err()
            .to_string();
        assert!(twice.contains("given twice"), "{twice}");
        let unknown = parse_stdio_options("probe-mcp", &strings(&["--verbose"]), None).unwrap_err().to_string();
        assert!(unknown.contains("unknown argument '--verbose' for `probe-mcp`"), "{unknown}");
    }

    #[test]
    fn install_reads_its_client_and_mode_in_either_order() {
        assert_eq!(parse_install_options("probe-mcp", &[]).unwrap(), (None, None));
        assert_eq!(
            parse_install_options("probe-mcp", &strings(&["claude", "--mode", "data"])).unwrap(),
            (Some("claude".to_string()), Some(Mode::Data))
        );
        assert_eq!(
            parse_install_options("probe-mcp", &strings(&["--mode", "estate", "vscode"])).unwrap(),
            (Some("vscode".to_string()), Some(Mode::Estate))
        );
        assert!(parse_install_options("probe-mcp", &strings(&["claude", "codex"])).is_err());
        assert!(parse_install_options("probe-mcp", &strings(&["--mode", "both"])).is_err());
    }
}
