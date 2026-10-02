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
        }
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
        Ok(McpHost::compose(self.modules, cp, gui_base, http_mode)?.into_server(http_mode))
    }

    /// Composes the host's modules into the parts of the server that own each contribution. The
    /// control plane and the transport are known by now, since a module's tools are built with them.
    fn compose(
        modules: Vec<McpModule>,
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
        let mut set = ModuleSet::default();
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
/// [client]`, `http [--bind <addr>] [--allowed-hosts <h1,h2>]`, or nothing for stdio.
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
            print_install(&host, args.get(2).map(String::as_str));
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
    // Over HTTP the server holds no credential of its own: every call runs as the bearer on the inbound request.
    let cp = Arc::new(ControlPlane::from_env(&identity, !http_mode));
    let gui_base = std::env::var("SQLFLOW_GUI_URL").unwrap_or_default();
    let composed = McpHost::compose(host.modules, cp, &gui_base, http_mode)?;

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
    tracing::info!("control plane: {}", composed.cp.base_url());
    match composed.links.base() {
        "" => tracing::info!(
            "GUI links: root-relative (set SQLFLOW_GUI_URL to make them absolute for clients outside the GUI)"
        ),
        base => tracing::info!("GUI links: {base}"),
    }

    let composed_cp = composed.cp.clone();
    let server = composed.into_server(http_mode);

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

fn print_install(host: &McpHost, client: Option<&str>) {
    let exe = std::env::current_exe()
        .map(|p| p.display().to_string())
        .unwrap_or_else(|_| host.name.clone());
    match install_text(&host.registration, &exe, client) {
        Ok(text) => println!("{text}"),
        Err(message) => eprintln!("{message}"),
    }
}

/// The ready-to-paste registration of the server for one MCP client.
fn install_text(registration: &str, exe: &str, client: Option<&str>) -> Result<String, String> {
    match client {
        Some("claude") => Ok(format!("claude mcp add {registration} -- {exe}")),
        Some("codex") => Ok(format!("[mcp_servers.{registration}]\ncommand = \"{exe}\"")),
        Some("cursor") | Some("vscode") | None => Ok(format!(
            "{{\n  \"mcpServers\": {{\n    \"{registration}\": {{\n      \"command\": \"{}\",\n      \"env\": {{ \"SQLFLOW_CONTROL_PLANE_URL\": \"http://localhost:8080\" }}\n    }}\n  }}\n}}",
            exe.replace('\\', "\\\\")
        )),
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
        McpHost::compose(modules, control_plane(), "https://gui.example.com", true)
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
            install_text("probe", "/opt/probe-mcp", Some("claude")).unwrap(),
            "claude mcp add probe -- /opt/probe-mcp"
        );
        assert_eq!(
            install_text("probe", "/opt/probe-mcp", Some("codex")).unwrap(),
            "[mcp_servers.probe]\ncommand = \"/opt/probe-mcp\""
        );
        let json = install_text("probe", "C:\\tools\\probe-mcp.exe", None).unwrap();
        assert!(json.contains("\"probe\": {"), "{json}");
        assert!(json.contains("C:\\\\tools\\\\probe-mcp.exe"), "{json}");
        assert!(install_text("probe", "x", Some("emacs")).unwrap_err().contains("Unknown client 'emacs'"));

        // SQLFlow's own registration is the one its users already have.
        assert_eq!(
            install_text(SQLFLOW_REGISTRATION, "sqlflow-mcp", Some("claude")).unwrap(),
            "claude mcp add sqlflow -- sqlflow-mcp"
        );
    }
}
