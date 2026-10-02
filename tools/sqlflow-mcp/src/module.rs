//! Host modules: what a host adds to the server beside SQLFlow's own tools.
//!
//! SQLFlow's control plane, CLI and GUI are composed with modules by the host that runs them, and a
//! module that adds flow kinds and endpoints there has questions of its own for an assistant to answer.
//! This is the same contract for the MCP server. A module contributes any of:
//!
//!   * **tools**: a type of its own carrying `#[tool]` methods, routed exactly as SQLFlow's are and
//!     dispatched through the one `call_tool` the server has, so a module's control-plane requests run
//!     as the calling user over HTTP without the module doing anything about it;
//!   * **instructions**: its section of the text a client reads at `initialize`;
//!   * **reference pages**: searched and fetched by the doc tools beside SQLFlow's corpus;
//!   * **link rules**: the GUI routes of the rows its endpoints return, so a result about one of the
//!     module's entities carries a destination like every other result does;
//!   * **key census files**: so `validate_flow`, `list_flow_keys` and `describe_flow_key` know the flow
//!     kinds and documents the module adds.
//!
//! The server knows nothing about a module beyond this. A module is registered through
//! [`McpHost::with_module`](crate::host::McpHost::with_module), never discovered, and everything it
//! declares is checked when the host starts: a name another module already took, a tool SQLFlow
//! already has, a page id that is already indexed or a census that does not parse stops the server
//! with a message naming the module, rather than surfacing later as a tool that is quietly missing.
//!
//! ```ignore
//! #[derive(Clone)]
//! struct ProbeTools { ctx: McpContext }
//!
//! #[tool_router(router = router)]
//! impl ProbeTools {
//!     #[tool(description = "List the probes the module's endpoint reports.")]
//!     async fn list_probes(&self, Parameters(_): Parameters<EmptyInput>) -> String {
//!         self.ctx.get("/api/v1/probes", &[]).await
//!     }
//! }
//!
//! let module = McpModule::new("probe")
//!     .tools(|ctx| (ProbeTools { ctx }, ProbeTools::router()))
//!     .instructions("PROBES:\n- list_probes lists them.");
//! sqlflow_mcp::run(McpHost::new("probe-mcp", env!("CARGO_PKG_VERSION")).with_module(module)).await
//! ```

use std::collections::HashMap;
use std::future::Future;
use std::pin::Pin;
use std::sync::Arc;

use rmcp::handler::server::router::tool::ToolRouter;
use rmcp::handler::server::tool::ToolCallContext;
use rmcp::model::{CallToolRequestParams, CallToolResult, Tool};
use rmcp::service::RequestContext;
use rmcp::RoleServer;
use serde_json::{Map, Value};

use crate::control_plane::ControlPlane;
use crate::docs::DocPage;
use crate::links::{GuiLinks, LinkRule};
use crate::server::{done, json_str, truncate_long_strings, with_subject};

/// How many characters of any one string a finished task's result keeps when a caller asks for it
/// trimmed: the shape and the head of a long text, with the whole document left on the task.
pub const TASK_TEXT_LIMIT: usize = 400;

/// What a module's tools reach the estate through: the control-plane client, the GUI routes, and
/// which transport the server runs on. Cheap to clone, and shared by every session of the process.
#[derive(Clone)]
pub struct McpContext {
    cp: Arc<ControlPlane>,
    links: GuiLinks,
    http_mode: bool,
}

impl McpContext {
    pub fn new(cp: Arc<ControlPlane>, links: GuiLinks, http_mode: bool) -> Self {
        McpContext { cp, links, http_mode }
    }

    /// The control-plane client. Every request it makes runs as the caller: the inbound bearer over
    /// HTTP, the stored sign-in over stdio.
    pub fn control_plane(&self) -> &ControlPlane {
        &self.cp
    }

    /// The GUI routes results are linked with, a module's own rules included.
    pub fn links(&self) -> &GuiLinks {
        &self.links
    }

    /// True when serving over HTTP, where a tool that reads the server host's files or runs a local
    /// program acts on the wrong machine and has to say so instead.
    pub fn is_http(&self) -> bool {
        self.http_mode
    }

    /// An authenticated `GET`, with a `links` object added to every row that names something the GUI
    /// can open.
    pub async fn read(&self, path: &str, query: &[(&str, String)]) -> anyhow::Result<Value> {
        let mut value = self.cp.get(path, query).await?;
        self.links.decorate(&mut value);
        Ok(value)
    }

    /// An authenticated `POST`, linked the same way.
    pub async fn send(&self, path: &str, body: Value) -> anyhow::Result<Value> {
        let mut value = self.cp.post(path, body).await?;
        self.links.decorate(&mut value);
        Ok(value)
    }

    /// The GET-and-render every read tool is: the control plane's payload, linked, as the text a
    /// tool returns. A failure is rendered as the tool's answer (`Error: ...`), which is what lets a
    /// model read why and try again.
    pub async fn get(&self, path: &str, query: &[(&str, String)]) -> String {
        done(self.read(path, query).await.map(|v| json_str(&v)))
    }

    /// The same, for a result that is ABOUT something the caller named rather than about the rows it
    /// returns: the rows are linked as everywhere else, and the subject's links go on the envelope (a
    /// bare array is wrapped in one, so there is an envelope to put them on).
    pub async fn get_about(
        &self,
        path: &str,
        query: &[(&str, String)],
        subject: (&str, &str),
        links: Value,
    ) -> String {
        done(
            self.read(path, query)
                .await
                .map(|v| json_str(&with_subject(v, subject, links))),
        )
    }

    /// The POST-and-render of a tool that asks the control plane for something.
    pub async fn post(&self, path: &str, body: Value) -> String {
        done(self.send(path, body).await.map(|v| json_str(&v)))
    }

    /// Long-polls one compute task to a terminal state and returns it. Each poll waits server-side
    /// for up to 20s; twelve rounds outlast the server's own task budget, so a hung task still
    /// terminates here carrying the task's own timeout error. `max_text` caps every string of the
    /// result (the full document stays on the task); `None` returns it as the node wrote it, which
    /// is what a caller that asked for the values themselves needs.
    pub async fn wait_for_task(
        &self,
        label: &str,
        task_id: &str,
        max_text: Option<usize>,
    ) -> anyhow::Result<Value> {
        for _ in 0..12 {
            let mut task = self
                .cp
                .get(
                    &format!("/api/v1/datasources/tasks/{task_id}"),
                    &[("waitMs", "20000".to_string())],
                )
                .await?;
            match task["status"].as_str() {
                Some("succeeded") | Some("failed") | Some("cancelled") | Some("skipped") => {
                    if let Some(max) = max_text {
                        truncate_long_strings(&mut task, max);
                    }
                    return Ok(task);
                }
                _ => {}
            }
        }

        anyhow::bail!(
            "The {label} task {task_id} did not reach a terminal state in time; check it with the \
             datasources task list."
        )
    }

    /// The task a control-plane endpoint queued, followed to its end: the endpoint's acknowledgement
    /// names the task (`taskId`), and the finished task is what the tool answers with.
    pub async fn follow_task(
        &self,
        label: &str,
        accepted: &Value,
        max_text: Option<usize>,
    ) -> anyhow::Result<Value> {
        let task_id = accepted["taskId"].as_str().ok_or_else(|| {
            anyhow::anyhow!("The control plane's accept response carried no taskId: {accepted}")
        })?;
        self.wait_for_task(label, task_id, max_text).await
    }
}

/// The future a module's tool call resolves through.
pub type ToolFuture<'a> =
    Pin<Box<dyn Future<Output = Result<CallToolResult, rmcp::ErrorData>> + Send + 'a>>;

/// The tools of one module, behind the server's one dispatch.
pub trait ModuleTools: Send + Sync {
    /// Every tool the module adds, as a client lists them.
    fn list(&self) -> Vec<Tool>;

    /// Runs one of them. Only called for a name [`list`](Self::list) reported.
    fn call(
        &self,
        request: CallToolRequestParams,
        context: RequestContext<RoleServer>,
    ) -> ToolFuture<'_>;
}

/// A module's tools as SQLFlow writes its own: a service type carrying `#[tool]` methods, and the
/// router the `#[tool_router]` macro generated for it.
struct RoutedTools<S> {
    service: S,
    router: ToolRouter<S>,
}

impl<S: Send + Sync + 'static> ModuleTools for RoutedTools<S> {
    fn list(&self) -> Vec<Tool> {
        self.router.list_all()
    }

    fn call(
        &self,
        request: CallToolRequestParams,
        context: RequestContext<RoleServer>,
    ) -> ToolFuture<'_> {
        Box::pin(async move {
            let call = ToolCallContext::new(&self.service, request, context);
            self.router.call(call).await
        })
    }
}

type ToolsBuilder = Box<dyn FnOnce(McpContext) -> Arc<dyn ModuleTools> + Send>;

/// A module a host composes into the server. Built with the methods below and handed to
/// [`McpHost::with_module`](crate::host::McpHost::with_module).
pub struct McpModule {
    name: String,
    tools: Option<ToolsBuilder>,
    instructions: Option<String>,
    docs: Vec<DocPage>,
    link_rules: Vec<LinkRule>,
    census: Vec<String>,
}

impl McpModule {
    /// A module named `name`: lowercase letters, digits and hyphens, starting with a letter, at most
    /// 64 characters, as a control-plane module is named. The name is what every startup error about
    /// the module carries, and two modules of one host may not share it. It is checked when the host
    /// starts.
    pub fn new(name: impl Into<String>) -> Self {
        McpModule {
            name: name.into(),
            tools: None,
            instructions: None,
            docs: Vec::new(),
            link_rules: Vec::new(),
            census: Vec::new(),
        }
    }

    pub fn name(&self) -> &str {
        &self.name
    }

    /// The module's tools: `build` receives the context its tools work through and returns the
    /// service carrying them with its router, `(Tools { ctx }, Tools::router())`. It runs once, when
    /// the host starts, and the service is shared by every session.
    pub fn tools<S, F>(mut self, build: F) -> Self
    where
        S: Send + Sync + 'static,
        F: FnOnce(McpContext) -> (S, ToolRouter<S>) + Send + 'static,
    {
        self.tools = Some(Box::new(move |ctx| {
            let (service, router) = build(ctx);
            Arc::new(RoutedTools { service, router })
        }));
        self
    }

    /// The module's section of the server instructions, appended after SQLFlow's own. Written as
    /// SQLFlow's are: which tool answers which question, and in what order to call them.
    pub fn instructions(mut self, text: impl Into<String>) -> Self {
        self.instructions = Some(text.into());
        self
    }

    /// Reference pages the doc tools search and fetch beside SQLFlow's corpus. A page id is unique
    /// across the whole index.
    pub fn docs(mut self, pages: impl IntoIterator<Item = DocPage>) -> Self {
        self.docs.extend(pages);
        self
    }

    /// A GUI link rule for the rows the module's endpoints return (see [`LinkRule`]).
    pub fn link_rule<F>(mut self, rule: F) -> Self
    where
        F: Fn(&GuiLinks, &Map<String, Value>) -> Option<Map<String, Value>> + Send + Sync + 'static,
    {
        self.link_rules.push(Arc::new(rule));
        self
    }

    /// A key census file of the module (the `keys` format, with a top-level `flowType` or
    /// `documentType`), so the flow-language tools document and check the documents it adds.
    pub fn census(mut self, json: impl Into<String>) -> Self {
        self.census.push(json.into());
        self
    }

    pub(crate) fn into_parts(self) -> ModuleParts {
        ModuleParts {
            name: self.name,
            tools: self.tools,
            instructions: self.instructions,
            docs: self.docs,
            link_rules: self.link_rules,
            census: self.census,
        }
    }
}

/// A module taken apart for the host to compose: each part goes to the piece of the server that
/// owns it (the docs index, the link decorator, the language engine, the dispatch).
pub(crate) struct ModuleParts {
    pub name: String,
    pub tools: Option<ToolsBuilder>,
    pub instructions: Option<String>,
    pub docs: Vec<DocPage>,
    pub link_rules: Vec<LinkRule>,
    pub census: Vec<String>,
}

/// Whether `name` is a module name: lowercase letters, digits and hyphens, starting with a letter,
/// at most 64 characters.
pub(crate) fn is_module_name(name: &str) -> bool {
    !name.is_empty()
        && name.len() <= 64
        && name.starts_with(|c: char| c.is_ascii_lowercase())
        && name
            .chars()
            .all(|c| c.is_ascii_lowercase() || c.is_ascii_digit() || c == '-')
}

/// Everything the modules of a host add to tool dispatch and to the instructions, built once and
/// shared by every server instance (one per session over HTTP).
#[derive(Default)]
pub struct ModuleSet {
    tools: Vec<Arc<dyn ModuleTools>>,
    /// Tool name to the index of the module tools that own it.
    owners: HashMap<String, usize>,
    /// Every module's tools, in registration order and by name within a module.
    listed: Vec<Tool>,
    instructions: String,
}

impl ModuleSet {
    /// Adds one module's tools. `taken` says whether a name is one of SQLFlow's own.
    pub(crate) fn add_tools(
        &mut self,
        module: &str,
        tools: Arc<dyn ModuleTools>,
        taken: impl Fn(&str) -> bool,
    ) -> Result<(), String> {
        let listed = tools.list();
        if listed.is_empty() {
            return Err(format!(
                "MCP module '{module}' registers tools and lists none; a module without tools leaves \
                 `tools` out"
            ));
        }
        for tool in &listed {
            let name = tool.name.as_ref();
            if taken(name) {
                return Err(format!(
                    "MCP module '{module}' adds a tool named '{name}', which SQLFlow's own server already \
                     has; a module's tools carry names of their own"
                ));
            }
            if self.owners.contains_key(name) {
                return Err(format!(
                    "MCP module '{module}' adds a tool named '{name}', which another module of this host \
                     already adds"
                ));
            }
        }
        let index = self.tools.len();
        for tool in &listed {
            self.owners.insert(tool.name.to_string(), index);
        }
        self.listed.extend(listed);
        self.tools.push(tools);
        Ok(())
    }

    pub(crate) fn add_instructions(&mut self, text: &str) {
        let text = text.trim();
        if text.is_empty() {
            return;
        }
        self.instructions.push_str("\n\n");
        self.instructions.push_str(text);
    }

    /// The module tools that own `name`, when a module added it.
    pub fn owner_of(&self, name: &str) -> Option<&Arc<dyn ModuleTools>> {
        self.owners.get(name).map(|index| &self.tools[*index])
    }

    /// Every tool the modules add.
    pub fn list(&self) -> &[Tool] {
        &self.listed
    }

    /// One module tool's definition by name.
    pub fn get(&self, name: &str) -> Option<&Tool> {
        self.listed.iter().find(|tool| tool.name == name)
    }

    /// The modules' instruction sections, each led by a blank line; empty when none wrote any.
    pub fn instructions(&self) -> &str {
        &self.instructions
    }
}
