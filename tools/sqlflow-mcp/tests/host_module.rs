//! A host composing the server with a module of its own, driven over both transports.
//!
//! The module here is a probe, not any product's: it adds two tools, a section of instructions, a
//! reference page and a link rule through the public API alone, exactly as a host crate would. The
//! control plane behind it is a fake that answers a handful of routes and echoes the `Authorization`
//! header it was called with, which is what shows whose credential a module's request carried.

use std::convert::Infallible;
use std::net::SocketAddr;
use std::sync::Arc;
use std::time::Duration;

use bytes::Bytes;
use http_body_util::{BodyExt, Full};
use hyper::body::Incoming;
use hyper::service::service_fn;
use hyper_util::rt::TokioIo;
use serde::Deserialize;
use serde_json::{json, Value};
use sqlflow_mcp::config::{StateStore, TokenCache};
use sqlflow_mcp::control_plane::ControlPlane;
use sqlflow_mcp::rmcp::handler::server::wrapper::Parameters;
use sqlflow_mcp::rmcp::{self, schemars, tool, tool_router, ServiceExt};
use sqlflow_mcp::modes::{self, Mode};
use sqlflow_mcp::server::SqlFlowMcp;
use sqlflow_mcp::{encode, http_server, json_str, DocMeta, DocPage, EmptyInput, McpContext, McpHost, McpModule};
use tokio::io::{AsyncBufReadExt, AsyncWriteExt, BufReader};

// ---- The probe module ------------------------------------------------------------------------------

#[derive(Debug, Deserialize, schemars::JsonSchema)]
struct RunProbeInput {
    /// The probe to run.
    name: String,
}

#[derive(Clone)]
struct ProbeTools {
    ctx: McpContext,
}

#[tool_router(router = router)]
impl ProbeTools {
    #[tool(description = "List the probes the control plane reports.")]
    async fn list_probes(&self, Parameters(_): Parameters<EmptyInput>) -> String {
        self.ctx.get("/api/v1/probes", &[]).await
    }

    #[tool(description = "Run one probe on a node and wait for what it found.")]
    async fn run_probe(&self, Parameters(input): Parameters<RunProbeInput>) -> String {
        let accepted = match self.ctx.send("/api/v1/probes/run", json!({ "name": input.name })).await {
            Ok(accepted) => accepted,
            Err(e) => return format!("Error: {e:#}"),
        };
        match self.ctx.follow_task("probe", &accepted, Some(40)).await {
            Ok(task) => json_str(&task),
            Err(e) => format!("Error: {e:#}"),
        }
    }
}

fn probe_module() -> McpModule {
    McpModule::new("probe")
        .tools(|ctx| (ProbeTools { ctx }, ProbeTools::router()))
        .instructions("PROBES:\n- list_probes lists every probe; run_probe runs one and waits for it.")
        .docs([DocPage {
            meta: DocMeta::page(
                "probe-concept",
                "Probes",
                "concept",
                "probe/concept.md",
                "What a probe is and when it runs.",
                &["probe"],
            ),
            body: "# Probes\n\nA probe asks a target whether it still answers.",
        }])
        .link_rule(|links, row| {
            let id = row.get("probeId")?.as_str()?;
            let mut found = serde_json::Map::new();
            found.insert("page".to_string(), links.route(&format!("/probes/{}", encode(id))).into());
            Some(found)
        })
}

fn probe_host() -> McpHost {
    McpHost::new("probe-mcp", "1.2.3")
        .registered_as("probe")
        .state_prefix("probe-mcp-test")
        .with_module(probe_module())
}

// ---- A control plane that answers the routes the tests call ------------------------------------------

async fn answer(req: hyper::Request<Incoming>) -> Result<hyper::Response<Full<Bytes>>, Infallible> {
    let authorization = req
        .headers()
        .get(http::header::AUTHORIZATION)
        .and_then(|v| v.to_str().ok())
        .unwrap_or("(none)")
        .to_string();
    let agent = req
        .headers()
        .get(http::header::USER_AGENT)
        .and_then(|v| v.to_str().ok())
        .unwrap_or_default()
        .to_string();
    let method = req.method().as_str().to_string();
    let path = req.uri().path().to_string();
    let sent = req.into_body().collect().await.map(|b| b.to_bytes()).unwrap_or_default();
    let body = match (method.as_str(), path.as_str()) {
        // The HTTP edge verifies every inbound bearer here before a tool runs; this control plane accepts any bearer
        // and refuses a request without one, as the real one refuses a missing or invalid token.
        ("GET", "/api/v1/me") if authorization.starts_with("Bearer ") => json!({ "subject": "probe-user" }),
        ("GET", "/api/v1/me") => {
            return Ok(hyper::Response::builder()
                .status(401)
                .body(Full::new(Bytes::from_static(b"no bearer")))
                .expect("a static response"));
        }
        // A run trigger answers with the request it was sent, as text, so a test reads exactly what
        // the tool produced rather than a copy the link decorator has been over.
        ("POST", "/api/v1/runs") => json!({
            "runId": "run-queued",
            "status": "queued",
            "received": String::from_utf8_lossy(&sent),
        }),
        ("GET", "/api/v1/probes") => json!([{
            "probeId": "wells a",
            "runId": "run-1",
            "seenAuthorization": authorization,
            "seenUserAgent": agent,
        }]),
        ("POST", "/api/v1/probes/run") => json!({ "taskId": "task-1", "status": "queued" }),
        ("GET", "/api/v1/datasources/tasks/task-1") => json!({
            "taskId": "task-1",
            "status": "succeeded",
            "seenAuthorization": authorization,
            "result": { "report": "x".repeat(200) },
        }),
        _ => {
            return Ok(hyper::Response::builder()
                .status(404)
                .body(Full::new(Bytes::from_static(b"no such route")))
                .expect("a static response"));
        }
    };
    Ok(hyper::Response::builder()
        .header(http::header::CONTENT_TYPE, "application/json")
        .body(Full::new(Bytes::from(body.to_string())))
        .expect("a json response"))
}

/// Starts the fake control plane on a port the system picks and returns its base URL.
async fn start_control_plane() -> String {
    let listener = tokio::net::TcpListener::bind(SocketAddr::from(([127, 0, 0, 1], 0)))
        .await
        .expect("a loopback port");
    let address = listener.local_addr().expect("a bound address");
    tokio::spawn(async move {
        loop {
            let Ok((stream, _)) = listener.accept().await else {
                return;
            };
            tokio::spawn(async move {
                let _ = hyper::server::conn::http1::Builder::new()
                    .serve_connection(TokioIo::new(stream), service_fn(answer))
                    .await;
            });
        }
    });
    format!("http://{address}")
}

fn control_plane(base_url: &str, token: Option<&str>) -> Arc<ControlPlane> {
    let identity = probe_host().identity().expect("the probe host is well named");
    // A client given a token holds it as a stdio server does; one given none is an HTTP server's, which presents only
    // the bearer of each inbound request.
    let ambient = token.is_some();
    let token = token.map(|secret| TokenCache {
        access_token: secret.to_string(),
        scope: "read operate".to_string(),
        expires_at: None,
        renews: false,
    });
    Arc::new(ControlPlane::new(&identity, base_url, token, ambient))
}

// ---- The server over HTTP ---------------------------------------------------------------------------

/// The composed server on a loopback port, stopped when the returned sender is dropped or fired.
async fn start_http(server: SqlFlowMcp) -> (String, tokio::sync::oneshot::Sender<()>) {
    let listener = tokio::net::TcpListener::bind(SocketAddr::from(([127, 0, 0, 1], 0)))
        .await
        .expect("a loopback port");
    let address = listener.local_addr().expect("a bound address");
    let (stop, stopped) = tokio::sync::oneshot::channel::<()>();
    tokio::spawn(async move {
        let shutdown = async move {
            let _ = stopped.await;
            Ok(())
        };
        http_server::serve_on(server, listener, Vec::new(), shutdown)
            .await
            .expect("the server runs until it is stopped");
    });
    (format!("http://{address}{}", http_server::MCP_PATH), stop)
}

/// One MCP session over streamable HTTP, as a remote client holds it: every request carries the
/// caller's bearer, the session id the server issued at `initialize`, and the protocol version it
/// negotiated there when that version has the header.
struct HttpSession {
    http: reqwest::Client,
    url: String,
    bearer: String,
    session: Option<String>,
    protocol: Option<String>,
    next_id: u64,
}

impl HttpSession {
    async fn open(url: &str, bearer: &str) -> (HttpSession, Value) {
        HttpSession::open_at(url, bearer, "2025-03-26").await
    }

    /// A session initialized naming `protocol`. From 2025-06-18 a client names the version it negotiated on every
    /// request after `initialize` (`MCP-Protocol-Version`), so the session does too.
    async fn open_at(url: &str, bearer: &str, protocol: &str) -> (HttpSession, Value) {
        let mut session = HttpSession {
            http: reqwest::Client::new(),
            url: url.to_string(),
            bearer: bearer.to_string(),
            session: None,
            protocol: None,
            next_id: 0,
        };
        let initialized = session
            .request(
                "initialize",
                json!({
                    "protocolVersion": protocol,
                    "capabilities": {},
                    "clientInfo": { "name": "host-module-test", "version": "0" },
                }),
            )
            .await;
        session.protocol = initialized["result"]["protocolVersion"]
            .as_str()
            .filter(|negotiated| *negotiated >= "2025-06-18")
            .map(String::from);
        session.post(json!({ "jsonrpc": "2.0", "method": "notifications/initialized" })).await;
        (session, initialized)
    }

    async fn post(&mut self, message: Value) -> reqwest::Response {
        let mut request = self
            .http
            .post(&self.url)
            .bearer_auth(&self.bearer)
            .header(http::header::ACCEPT, "application/json, text/event-stream")
            .json(&message);
        if let Some(session) = &self.session {
            request = request.header("mcp-session-id", session);
        }
        if let Some(protocol) = &self.protocol {
            request = request.header("mcp-protocol-version", protocol);
        }
        let response = request.send().await.expect("the server answers");
        assert!(
            response.status().is_success(),
            "{} answered {}",
            message["method"],
            response.status()
        );
        if let Some(id) = response.headers().get("mcp-session-id") {
            self.session = Some(id.to_str().expect("a printable session id").to_string());
        }
        response
    }

    /// Sends one request and returns the JSON-RPC message answering it, read off the event stream
    /// (or the plain JSON body) the server replies with.
    async fn request(&mut self, method: &str, params: Value) -> Value {
        self.next_id += 1;
        let id = self.next_id;
        let mut response = self
            .post(json!({ "jsonrpc": "2.0", "id": id, "method": method, "params": params }))
            .await;

        let mut text = String::new();
        let answer = tokio::time::timeout(Duration::from_secs(30), async {
            loop {
                if let Some(found) = answered(&text, id) {
                    return found;
                }
                match response.chunk().await.expect("the response body is readable") {
                    Some(chunk) => text.push_str(&String::from_utf8_lossy(&chunk)),
                    None => panic!("the reply to {method} ended without an answer: {text}"),
                }
            }
        })
        .await;
        answer.unwrap_or_else(|_| panic!("no answer to {method} within 30s"))
    }

    /// Calls a tool and returns the text of its result and whether the server marked it an error.
    async fn call_checked(&mut self, tool: &str, arguments: Value) -> (String, bool) {
        let answer = self.request("tools/call", json!({ "name": tool, "arguments": arguments })).await;
        let text = answer["result"]["content"][0]["text"].as_str().unwrap_or_default().to_string();
        (text, answer["result"]["isError"] == json!(true))
    }

    /// Calls a tool and returns the text of its result.
    async fn call(&mut self, tool: &str, arguments: Value) -> String {
        let answer = self.request("tools/call", json!({ "name": tool, "arguments": arguments })).await;
        answer["result"]["content"][0]["text"]
            .as_str()
            .unwrap_or_else(|| panic!("{tool} returned no text: {answer}"))
            .to_string()
    }
}

/// The JSON-RPC message with `id` in what has been read so far: a line of an event stream
/// (`data: {...}`), or the whole body when the server answered with plain JSON.
fn answered(text: &str, id: u64) -> Option<Value> {
    let candidates = text
        .lines()
        .filter_map(|line| line.strip_prefix("data:"))
        .map(str::trim)
        .chain(std::iter::once(text.trim()));
    for candidate in candidates {
        if let Ok(message) = serde_json::from_str::<Value>(candidate) {
            if message["id"] == json!(id) {
                return Some(message);
            }
        }
    }
    None
}

fn tool_names(listing: &Value) -> Vec<String> {
    listing["result"]["tools"]
        .as_array()
        .expect("a tool list")
        .iter()
        .map(|tool| tool["name"].as_str().expect("a tool name").to_string())
        .collect()
}

#[tokio::test(flavor = "multi_thread")]
async fn a_module_is_served_beside_sqlflows_own_tools_over_http() {
    let estate = start_control_plane().await;
    let server = probe_host()
        .server(control_plane(&estate, None), "https://gui.example.com/", true)
        .expect("the probe host composes");
    let (url, _stop) = start_http(server).await;

    let (mut session, initialized) = HttpSession::open(&url, "caller-a").await;

    // The server reports the host's program, and the module's section follows SQLFlow's instructions.
    assert_eq!(initialized["result"]["serverInfo"]["name"], json!("probe-mcp"));
    assert_eq!(initialized["result"]["serverInfo"]["version"], json!("1.2.3"));
    let instructions = initialized["result"]["instructions"].as_str().expect("instructions");
    assert!(instructions.starts_with("SQLFlow MCP server."), "{instructions}");
    assert!(instructions.contains("Auth is per request"), "the HTTP setup text: {instructions}");
    assert!(
        instructions.ends_with("\n\nPROBES:\n- list_probes lists every probe; run_probe runs one and waits for it."),
        "{instructions}"
    );

    // Both sets of tools are listed: a session naming no mode lists every tool, the mode tools included. Over HTTP
    // the tools that sign in or move the server are not among them, since every call carries its own bearer.
    let names = tool_names(&session.request("tools/list", json!({})).await);
    for tool in ["list_probes", "run_probe", "list_repos", "search_docs", "validate_flow", "trigger_run", "run_query"]
        .iter()
        .chain(&[modes::GET_MODE, modes::START_DATA_MODE, modes::END_DATA_MODE])
    {
        assert!(names.contains(&tool.to_string()), "{tool} is not listed: {names:?}");
    }
    for tool in SIGN_IN_TOOLS {
        assert!(!names.contains(&tool.to_string()), "{tool} is listed over HTTP: {names:?}");
    }
    assert_eq!(names.len(), listed_over_http(None, &[]).len() + 2);
    assert_eq!(names.iter().filter(|n| n.as_str() == "list_probes").count(), 1);

    // The module's tool runs as the caller, and its row carries the module's link on the host's GUI.
    let probes: Value = serde_json::from_str(&session.call("list_probes", json!({})).await).expect("json");
    assert_eq!(probes[0]["seenAuthorization"], json!("Bearer caller-a"));
    assert_eq!(probes[0]["seenUserAgent"], json!("probe-mcp/1.2.3"));
    assert_eq!(probes[0]["links"]["page"], json!("https://gui.example.com/probes/wells%20a"));
    assert_eq!(probes[0]["links"]["run"], json!("https://gui.example.com/runs/run-1"));

    // A task the module queued is followed through the shared poll, as the caller, and trimmed as asked.
    let task: Value =
        serde_json::from_str(&session.call("run_probe", json!({ "name": "wells" })).await).expect("json");
    assert_eq!(task["status"], json!("succeeded"));
    assert_eq!(task["seenAuthorization"], json!("Bearer caller-a"));
    assert_eq!(task["result"]["report"], json!(format!("{}... [truncated]", "x".repeat(40))));

    // SQLFlow's own tools are dispatched as before, and see the module's page in the one index.
    let found: Value =
        serde_json::from_str(&session.call("search_docs", json!({ "query": "probe" })).await).expect("json");
    assert_eq!(found["results"][0]["id"], json!("probe-concept"));
    assert!(session.call("get_doc", json!({ "id": "probe-concept" })).await.contains("still answers"));
    assert!(session.call("get_doc", json!({ "id": "cli-run" })).await.starts_with("# "));

    // A bad argument is the tool's own readable refusal, and an unknown tool is refused by name.
    let refused = session.request("tools/call", json!({ "name": "run_probe", "arguments": {} })).await;
    assert_eq!(refused["result"]["isError"], json!(true), "{refused}");
    let unknown = session.request("tools/call", json!({ "name": "no_such_tool", "arguments": {} })).await;
    assert!(unknown["error"]["message"].as_str().unwrap_or_default().contains("tool not found"), "{unknown}");
}

#[tokio::test(flavor = "multi_thread")]
async fn each_http_caller_reaches_the_control_plane_as_itself() {
    let estate = start_control_plane().await;
    // The server holds a token of its own, which over HTTP must never be the one a request carries.
    let server = probe_host()
        .server(control_plane(&estate, Some("the-servers-own")), "", true)
        .expect("the probe host composes");
    let (url, _stop) = start_http(server).await;

    let (mut first, _) = HttpSession::open(&url, "caller-a").await;
    let (mut second, _) = HttpSession::open(&url, "caller-b").await;

    for _ in 0..3 {
        let (a, b) = tokio::join!(first.call("list_probes", json!({})), second.call("list_probes", json!({})));
        let (a, b): (Value, Value) = (serde_json::from_str(&a).unwrap(), serde_json::from_str(&b).unwrap());
        assert_eq!(a[0]["seenAuthorization"], json!("Bearer caller-a"));
        assert_eq!(b[0]["seenAuthorization"], json!("Bearer caller-b"));
        // No GUI base was given, so links stay root-relative.
        assert_eq!(a[0]["links"]["page"], json!("/probes/wells%20a"));
    }
}

#[tokio::test(flavor = "multi_thread")]
async fn a_request_without_a_bearer_is_challenged_in_the_hosts_name() {
    let estate = start_control_plane().await;
    let server = probe_host().server(control_plane(&estate, None), "", true).expect("the probe host composes");
    let (url, _stop) = start_http(server).await;

    let response = reqwest::Client::new()
        .post(&url)
        .header(http::header::ACCEPT, "application/json, text/event-stream")
        .json(&json!({ "jsonrpc": "2.0", "id": 1, "method": "tools/list", "params": {} }))
        .send()
        .await
        .expect("the server answers");

    assert_eq!(response.status(), reqwest::StatusCode::UNAUTHORIZED);
    assert_eq!(
        response.headers().get(http::header::WWW_AUTHENTICATE).and_then(|v| v.to_str().ok()),
        Some("Bearer realm=\"probe-mcp\"")
    );
}

/// The request body a `trigger_run` call produced, as the fake control plane received it.
fn received(answer: &str) -> Value {
    let answer: Value = serde_json::from_str(answer).unwrap_or_else(|e| panic!("{e}: {answer}"));
    serde_json::from_str(answer["received"].as_str().expect("the echoed request")).expect("a json request")
}

#[tokio::test(flavor = "multi_thread")]
async fn a_run_of_a_registered_kind_carries_the_kinds_own_arguments() {
    let estate = start_control_plane().await;
    let server = probe_host().server(control_plane(&estate, None), "", true).expect("the probe host composes");
    let (url, _stop) = start_http(server).await;
    let (mut session, _) = HttpSession::open(&url, "caller-a").await;

    // A flow of one of SQLFlow's own kinds: the request is what it always was, with no kind argument.
    let plain = session
        .call("trigger_run", json!({ "repoId": "repo-1", "flowName": "orders_ing", "fullLoad": true }))
        .await;
    assert_eq!(received(&plain), json!({ "repoId": "repo-1", "flowName": "orders_ing", "fullLoad": true }));
    let plain: Value = serde_json::from_str(&plain).expect("json");
    assert_eq!(plain["links"]["page"], json!("/runs/run-queued"));

    // A flow of a kind a module registered: the operation, the values and the payload travel as given.
    let kind = session
        .call(
            "trigger_run",
            json!({
                "repoId": "repo-1",
                "flowName": "wells_probe",
                "operation": " verify ",
                "values": { "region": "north" },
                "payload": { "targets": ["a", "b"], "force": true },
            }),
        )
        .await;
    assert_eq!(
        received(&kind),
        json!({
            "repoId": "repo-1",
            "flowName": "wells_probe",
            "fullLoad": false,
            "operation": "verify",
            "values": { "region": "north" },
            "payload": { "targets": ["a", "b"], "force": true },
        })
    );

    // Blank or empty kind arguments are left out rather than sent as blanks the kind would refuse.
    let blank = session
        .call(
            "trigger_run",
            json!({ "repoId": "repo-1", "flowName": "wells_probe", "operation": " ", "values": {}, "payload": null }),
        )
        .await;
    assert_eq!(received(&blank), json!({ "repoId": "repo-1", "flowName": "wells_probe", "fullLoad": false }));

    // A payload that is not an object is refused here, with nothing queued.
    let refused = session
        .call("trigger_run", json!({ "repoId": "repo-1", "flowName": "wells_probe", "payload": ["a"] }))
        .await;
    assert!(refused.starts_with("Error: payload is a JSON object"), "{refused}");
}

#[tokio::test(flavor = "multi_thread")]
async fn a_tool_the_host_leaves_out_is_neither_listed_nor_callable() {
    let estate = start_control_plane().await;
    let server = probe_host()
        .without_tools(["run_query", "prepare_query"])
        .server(control_plane(&estate, None), "", true)
        .expect("the probe host composes");
    let (url, _stop) = start_http(server).await;
    let (mut session, initialized) = HttpSession::open(&url, "caller-a").await;

    let names = tool_names(&session.request("tools/list", json!({})).await);
    assert!(!names.contains(&"run_query".to_string()) && !names.contains(&"prepare_query".to_string()), "{names:?}");
    // Everything else of SQLFlow's, and the module's own, is still there.
    assert_eq!(names.len(), listed_over_http(None, &["run_query", "prepare_query"]).len() + 2);
    assert!(names.contains(&"list_repos".to_string()) && names.contains(&"list_probes".to_string()));

    let called = session
        .request("tools/call", json!({ "name": "run_query", "arguments": { "planId": "p-1" } }))
        .await;
    assert!(called["error"]["message"].as_str().unwrap_or_default().contains("tool not found"), "{called}");

    // The instructions end by saying so, after the module's own section.
    let instructions = initialized["result"]["instructions"].as_str().expect("instructions");
    assert!(instructions.contains("run_probe runs one and waits for it.\n\nNot offered by this server"), "{instructions}");
    assert!(instructions.trim_end().ends_with("say so when a question needs one."), "{instructions}");
    assert!(instructions.contains("prepare_query, run_query"), "{instructions}");
}

/// A session a test calls tools through, whichever transport carries it: a tool's text answer, and whether the server
/// marked it an error.
trait Caller {
    async fn tool(&mut self, name: &str, arguments: Value) -> (String, bool);
}

impl Caller for HttpSession {
    async fn tool(&mut self, name: &str, arguments: Value) -> (String, bool) {
        self.call_checked(name, arguments).await
    }
}

impl Caller for LineSession {
    async fn tool(&mut self, name: &str, arguments: Value) -> (String, bool) {
        self.call(name, arguments).await
    }
}

/// Whether `text` is the server's refusal of a tool outside the session's mode, which it gives without running the tool.
fn is_mode_refusal(text: &str) -> bool {
    text.contains(" answers questions about the estate, and this ")
        || text.contains(" answers a question from warehouse data, and this ")
        || text.contains(" is not offered here. This server is registered in ")
}

/// The mode get_mode reports for `session`.
async fn mode_of(session: &mut impl Caller) -> Value {
    let (text, is_error) = session.tool("get_mode", json!({})).await;
    assert!(!is_error, "get_mode is answered in every session: {text}");
    serde_json::from_str(&text).expect("get_mode answers JSON")
}

/// The tools that sign in, keep a token or point the server elsewhere: a stdio session's own, not listed over HTTP.
const SIGN_IN_TOOLS: &[&str] = &["login", "check_auth_status", "set_access_token", "logout", "set_control_plane_url"];

/// SQLFlow's tools an HTTP session lists, the host leaving out `withheld`: one its client opened in `mode`, or one that
/// names none (every tool).
fn listed_over_http(mode: Option<Mode>, withheld: &[&str]) -> Vec<String> {
    let mut names: Vec<String> = SqlFlowMcp::tool_names()
        .into_iter()
        .filter(|name| mode.is_none_or(|mode| modes::named_lists(mode, name)))
        .filter(|name| !SIGN_IN_TOOLS.contains(&name.as_str()) && !withheld.contains(&name.as_str()))
        .collect();
    names.sort();
    names
}

/// A client that opens a session per message (a model provider's connector) reads the person's command itself and names
/// the conversation's mode on the address. The session lists that mode's tools and no switch, answers nothing of the
/// other mode, and stays in its mode whatever the model calls; a session beside it keeps its own.
#[tokio::test(flavor = "multi_thread")]
async fn an_http_session_named_in_a_mode_stays_in_it_and_offers_no_switch() {
    let estate = start_control_plane().await;
    let server = probe_host()
        .server(control_plane(&estate, None), "", true)
        .expect("the probe host composes");
    let (url, _stop) = start_http(server).await;

    let (mut data, _) = HttpSession::open(&format!("{url}?surface=assistant&mode=data"), "caller-a").await;
    let mut listed = tool_names(&data.request("tools/list", json!({})).await);
    listed.sort();
    assert_eq!(listed, listed_over_http(Some(Mode::Data), &[]), "a data-mode session lists its own tools and the shared ones");
    for absent in ["start_data_mode", "end_data_mode", "list_probes", "list_repos"] {
        assert!(!listed.contains(&absent.to_string()), "{absent}: {listed:?}");
    }

    // get_mode says so, that the client set it, and that the session does not switch.
    let mode = mode_of(&mut data).await;
    assert_eq!(mode["mode"], json!("data"), "{mode}");
    assert_eq!((mode["switches"].clone(), mode["listsEveryTool"].clone()), (json!(false), json!(false)), "{mode}");
    assert!(mode["setBy"].as_str().unwrap_or_default().contains("client"), "{mode}");

    // An estate tool is refused with the command that returns to the estate, naming no switch the session lacks.
    for tool in ["list_repos", "list_probes", "search_all"] {
        let (refused, is_error) = data.call_checked(tool, json!({})).await;
        assert!(is_error && is_mode_refusal(&refused) && refused.contains("`!exit`"), "{tool}: {refused}");
        assert!(!refused.contains("end_data_mode"), "{refused}");
    }

    // A switch called anyway moves nothing: to the mode it is in, it says so; to the other, it refuses.
    let (stay, is_error) = data.call_checked("start_data_mode", json!({ "message": "!data which regions" })).await;
    let stay: Value = serde_json::from_str(&stay).expect("json");
    assert!(!is_error && stay["switched"] == json!(false) && stay["mode"] == json!("data"), "{stay}");
    let (leave, is_error) = data.call_checked("end_data_mode", json!({ "message": "!exit" })).await;
    assert!(is_error && leave.contains("`!exit`") && leave.contains("next message"), "{leave}");
    assert_eq!(mode_of(&mut data).await["mode"], json!("data"));
    let (refused, is_error) = data.call_checked("list_repos", json!({})).await;
    assert!(is_error && is_mode_refusal(&refused), "still data mode after a refused switch: {refused}");

    // An estate-mode session lists the estate's tools and the module's, none that only data mode needs and no switch.
    let (mut estate_session, _) = HttpSession::open(&format!("{url}?surface=assistant&mode=estate"), "caller-b").await;
    let mut listed = tool_names(&estate_session.request("tools/list", json!({})).await);
    listed.retain(|name| name != "list_probes" && name != "run_probe");
    listed.sort();
    assert_eq!(listed, listed_over_http(Some(Mode::Estate), &[]));
    assert!(!listed.contains(&"start_data_mode".to_string()), "{listed:?}");
    let (refused, is_error) = estate_session.call_checked("run_query", json!({ "planId": "p-1" })).await;
    assert!(is_error && is_mode_refusal(&refused) && refused.contains("`!data`"), "{refused}");
    assert!(!refused.contains("start_data_mode"), "{refused}");
    let (start, is_error) = estate_session.call_checked("start_data_mode", json!({ "message": "!data which regions" })).await;
    assert!(is_error && start.contains("`!data`"), "{start}");
    assert_eq!(mode_of(&mut estate_session).await["mode"], json!("estate"));

    // The data session is where it was, and a fresh one opens in the mode its address names.
    assert_eq!(mode_of(&mut data).await["mode"], json!("data"));
    let (mut fresh, _) = HttpSession::open(&format!("{url}?mode=data"), "caller-c").await;
    assert!(tool_names(&fresh.request("tools/list", json!({})).await).contains(&"run_query".to_string()));
}

/// A session opened in a mode is given that mode's instructions at `initialize`, naming only what it lists: a data-mode
/// session the modes, the data rules and the rules every answer follows, without the estate's guide, the setup its
/// bearer already did, or a module's section; an estate-mode session everything but the data rules.
#[tokio::test(flavor = "multi_thread")]
async fn an_http_session_is_given_the_instructions_of_the_mode_its_address_names() {
    let estate = start_control_plane().await;
    let server = probe_host()
        .server(control_plane(&estate, None), "", true)
        .expect("the probe host composes");
    let (url, _stop) = start_http(server).await;

    let (_, data) = HttpSession::open(&format!("{url}?surface=assistant&mode=data"), "caller-a").await;
    let data = data["result"]["instructions"].as_str().expect("instructions").to_string();
    assert!(data.starts_with("SQLFlow MCP server.\n\nDATA MODE, BEFORE ANYTHING ELSE: the person's command"), "{data}");
    for switch in ["start_data_mode", "end_data_mode", "MODES AND ROUTING"] {
        assert!(!data.contains(switch), "a session its client named is told about {switch}");
    }
    assert!(data.contains("ANSWERING A QUESTION FROM WAREHOUSE DATA") && data.contains("`grounding`"), "{data}");
    assert!(data.contains("Grounding: state only values a tool's result contains"), "{data}");
    for estate_text in ["Two tiers of tools", "search_all FIRST", "ONLINE (needs the control plane)", "run_probe runs one"] {
        assert!(!data.contains(estate_text), "a data-mode session is told: {estate_text}");
    }

    let (_, estate_answer) = HttpSession::open(&format!("{url}?surface=assistant&mode=estate"), "caller-b").await;
    let estate_text = estate_answer["result"]["instructions"].as_str().expect("instructions").to_string();
    assert!(estate_text.contains("search_all FIRST") && estate_text.ends_with("run_probe runs one and waits for it."), "{estate_text}");
    assert!(estate_text.starts_with("SQLFlow MCP server.\n\nESTATE MODE, BEFORE ANYTHING ELSE"), "{estate_text}");
    for switch in ["start_data_mode", "end_data_mode", "MODES AND ROUTING"] {
        assert!(!estate_text.contains(switch), "a session its client named is told about {switch}");
    }
    assert!(!estate_text.contains("ANSWERING A QUESTION FROM WAREHOUSE DATA"), "{estate_text}");
    assert!(!estate_text.contains("Writing SQL for a business question"), "{estate_text}");

    let (_, every) = HttpSession::open(&url, "caller-c").await;
    let every = every["result"]["instructions"].as_str().expect("instructions").to_string();
    assert!(every.contains("Writing SQL for a business question") && every.contains("search_all FIRST"), "{every}");
    assert!(every.starts_with("SQLFlow MCP server.\n\nMODES AND ROUTING, BEFORE ANYTHING ELSE"), "{every}");
    assert!(data.len() * 2 < every.len(), "data mode is given {} of {} characters", data.len(), every.len());
}

// ---- The server over stdio --------------------------------------------------------------------------

/// A client's end of a stdio session: one JSON-RPC message per line, each way.
struct LineSession {
    /// The methods of the notifications the server sent, in order, read while waiting for answers.
    notifications: Vec<String>,
    reader: BufReader<tokio::io::ReadHalf<tokio::io::DuplexStream>>,
    writer: tokio::io::WriteHalf<tokio::io::DuplexStream>,
    next_id: u64,
}

impl LineSession {
    async fn send(&mut self, message: Value) {
        self.writer.write_all(format!("{message}\n").as_bytes()).await.expect("the server reads");
        self.writer.flush().await.expect("the server reads");
    }

    async fn request(&mut self, method: &str, params: Value) -> Value {
        self.next_id += 1;
        let id = self.next_id;
        self.send(json!({ "jsonrpc": "2.0", "id": id, "method": method, "params": params })).await;
        let answer = tokio::time::timeout(Duration::from_secs(30), async {
            loop {
                let mut line = String::new();
                let read = self.reader.read_line(&mut line).await.expect("the server writes");
                assert!(read > 0, "the server closed the session before answering {method}");
                if let Ok(message) = serde_json::from_str::<Value>(line.trim()) {
                    if message["id"] == json!(id) {
                        return message;
                    }
                    if message.get("id").is_none() {
                        if let Some(notification) = message["method"].as_str() {
                            self.notifications.push(notification.to_string());
                        }
                    }
                }
            }
        })
        .await;
        answer.unwrap_or_else(|_| panic!("no answer to {method} within 30s"))
    }

    /// A tool's text answer, and whether the server marked it an error.
    async fn call(&mut self, name: &str, arguments: Value) -> (String, bool) {
        let called = self.request("tools/call", json!({ "name": name, "arguments": arguments })).await;
        let text = called["result"]["content"][0]["text"].as_str().unwrap_or_default().to_string();
        (text, called["result"]["isError"] == json!(true))
    }
}

/// Serves `server` over an in-memory stdio pair: the client's end, before it has sent anything.
fn serve_stdio(server: SqlFlowMcp) -> LineSession {
    let (client_end, server_end) = tokio::io::duplex(1 << 16);
    let (server_read, server_write) = tokio::io::split(server_end);
    tokio::spawn(async move {
        let running = server.serve((server_read, server_write)).await.expect("the session starts");
        let _ = running.waiting().await;
    });
    let (client_read, client_write) = tokio::io::split(client_end);
    LineSession { reader: BufReader::new(client_read), writer: client_write, next_id: 0, notifications: Vec::new() }
}

impl LineSession {
    /// Initializes the session naming `protocol`: the answer to `initialize`.
    async fn initialize(&mut self, protocol: &str) -> Value {
        let initialized = self
            .request(
                "initialize",
                json!({
                    "protocolVersion": protocol,
                    "capabilities": {},
                    "clientInfo": { "name": "host-module-test", "version": "0" },
                }),
            )
            .await;
        self.send(json!({ "jsonrpc": "2.0", "method": "notifications/initialized" })).await;
        initialized
    }
}

/// Serves `server` over an in-memory stdio pair and initializes a session: the session, and the answer to `initialize`.
async fn open_stdio(server: SqlFlowMcp) -> (LineSession, Value) {
    let mut session = serve_stdio(server);
    let initialized = session.initialize("2025-03-26").await;
    (session, initialized)
}

#[tokio::test(flavor = "multi_thread")]
async fn over_stdio_a_module_tool_uses_the_servers_own_sign_in() {
    let estate = start_control_plane().await;
    let server = probe_host()
        .server(control_plane(&estate, Some("stored-sign-in")), "", false)
        .expect("the probe host composes");
    let (mut session, initialized) = open_stdio(server).await;

    let instructions = initialized["result"]["instructions"].as_str().expect("instructions");
    // SQLFlow's introduction, then the modes and the `!` commands, since find_similar_questions is offered.
    assert!(instructions.starts_with("SQLFlow MCP server.\n\nMODES AND ROUTING, BEFORE ANYTHING ELSE"), "{instructions}");
    assert!(instructions.contains("`!exit`") && instructions.contains("end_data_mode"), "{instructions}");
    assert!(instructions.contains("\n\nTwo tiers of tools:"), "{instructions}");
    assert!(instructions.contains("login (device flow)"), "the stdio setup text: {instructions}");
    assert!(instructions.contains("discover_source"), "{instructions}");
    assert!(instructions.ends_with("run_probe runs one and waits for it."), "{instructions}");
    assert_eq!(initialized["result"]["serverInfo"]["name"], json!("probe-mcp"));

    let names = tool_names(&session.request("tools/list", json!({})).await);
    assert!(names.contains(&"list_probes".to_string()) && names.contains(&"login".to_string()), "{names:?}");

    let called = session.request("tools/call", json!({ "name": "list_probes", "arguments": {} })).await;
    let probes: Value =
        serde_json::from_str(called["result"]["content"][0]["text"].as_str().expect("text")).expect("json");
    assert_eq!(probes[0]["seenAuthorization"], json!("Bearer stored-sign-in"));
    assert_eq!(probes[0]["links"]["page"], json!("/probes/wells%20a"));
}

/// The request metadata a client of a protocol version without `initialize` (2026-07-28 on) sends in place of a session.
fn sessionless_meta() -> Value {
    json!({
        "io.modelcontextprotocol/protocolVersion": "2026-07-28",
        "io.modelcontextprotocol/clientInfo": { "name": "host-module-test", "version": "0" },
        "io.modelcontextprotocol/clientCapabilities": {},
    })
}

/// The protocol versions the server negotiates: each one that opens with `initialize`.
const NEGOTIATED: [&str; 4] = ["2024-11-05", "2025-03-26", "2025-06-18", "2025-11-25"];

/// A session is where the person's mode switch lives, so a client of a version that opens no session is not served
/// without one: over either transport its request is refused naming the versions the server negotiates, and nothing
/// runs. Naming the later version at `initialize` is answered with the newest that has the handshake, and the session it
/// opens keeps the switch from one request to the next, as every session does.
#[tokio::test(flavor = "multi_thread")]
async fn a_client_that_opens_no_session_is_told_the_versions_it_can_initialize_with() {
    let estate = start_control_plane().await;

    let stdio = probe_host()
        .server(control_plane(&estate, Some("stored-sign-in")), "", false)
        .expect("the probe host composes");
    let mut line = serve_stdio(stdio);
    let refused = line.request("tools/list", json!({ "_meta": sessionless_meta() })).await;
    assert_eq!(refused["error"]["data"]["requested"], json!("2026-07-28"), "{refused}");
    assert_eq!(refused["error"]["data"]["supported"], json!(NEGOTIATED), "{refused}");
    assert!(refused.get("result").is_none(), "{refused}");
    // The connection is still open, waiting for `initialize`.
    let initialized = line.initialize("2026-07-28").await;
    assert_eq!(initialized["result"]["protocolVersion"], json!("2025-11-25"), "{initialized}");
    let (started, is_error) = line.call("start_data_mode", json!({ "message": "!data which regions" })).await;
    assert!(!is_error, "{started}");
    assert_eq!(mode_of(&mut line).await["mode"], json!("data"));

    let http = probe_host().server(control_plane(&estate, None), "", true).expect("the probe host composes");
    let (url, _stop) = start_http(http).await;
    let response = reqwest::Client::new()
        .post(&url)
        .bearer_auth("caller-a")
        .header(http::header::ACCEPT, "application/json, text/event-stream")
        .header("mcp-protocol-version", "2026-07-28")
        .header("mcp-method", "tools/list")
        .json(&json!({ "jsonrpc": "2.0", "id": 1, "method": "tools/list", "params": { "_meta": sessionless_meta() } }))
        .send()
        .await
        .expect("the server answers");
    assert_eq!(response.status(), reqwest::StatusCode::BAD_REQUEST);
    assert!(response.headers().get("mcp-session-id").is_none(), "a refused request opens no session");
    let text = response.text().await.expect("a readable body");
    let refused = answered(&text, 1).unwrap_or_else(|| panic!("no JSON-RPC answer: {text}"));
    assert_eq!(refused["error"]["data"]["supported"], json!(NEGOTIATED), "{refused}");
    assert!(refused.get("result").is_none(), "{refused}");

    let (mut remote, initialized) = HttpSession::open_at(&url, "caller-b", "2026-07-28").await;
    assert_eq!(initialized["result"]["protocolVersion"], json!("2025-11-25"), "{initialized}");
    let (started, is_error) = remote.call_checked("start_data_mode", json!({ "message": "!data which regions" })).await;
    assert!(!is_error, "{started}");
    assert_eq!(mode_of(&mut remote).await["mode"], json!("data"));
}

/// A stdio session that names no mode (an editor's single registration) lists every tool, since its client keeps one
/// session across chats and may not refresh its list, and starts in estate mode. Only the person's command, relayed to
/// a switch with their message, moves it; in each mode it answers only that mode's tools, and its list never changes.
#[tokio::test(flavor = "multi_thread")]
async fn a_stdio_session_that_names_no_mode_switches_only_on_the_persons_command() {
    let estate = start_control_plane().await;
    let server = probe_host()
        .server(control_plane(&estate, Some("stored-sign-in")), "", false)
        .expect("the probe host composes");
    let (mut session, initialized) = open_stdio(server).await;

    // The list a session is given never changes, so no change is announced as a capability.
    assert!(initialized["result"]["capabilities"]["tools"]["listChanged"].is_null(), "{initialized}");
    let names = tool_names(&session.request("tools/list", json!({})).await);
    for tool in ["find_similar_questions", "run_query", "list_runs", "list_probes", "login", "get_mode", "start_data_mode", "end_data_mode"] {
        assert!(names.contains(&tool.to_string()), "{tool}: {names:?}");
    }

    let mode = mode_of(&mut session).await;
    assert_eq!(mode["mode"], json!("estate"), "{mode}");
    assert_eq!((mode["switches"].clone(), mode["listsEveryTool"].clone()), (json!(true), json!(true)), "{mode}");
    assert!(mode["setBy"].as_str().unwrap_or_default().contains("default"), "{mode}");

    // In estate mode a data tool is refused, naming the switch and the command it needs.
    let (refused, is_error) = session.call("find_similar_questions", json!({ "question": "which regions" })).await;
    assert!(is_error && is_mode_refusal(&refused) && refused.contains("start_data_mode") && refused.contains("`!data`"), "{refused}");

    // A message without the command, or with the other mode's, switches nothing.
    for message in ["which suppliers ship to norway", "switch to data mode", "data: which regions", ""] {
        let (answer, is_error) = session.call("start_data_mode", json!({ "message": message })).await;
        assert!(is_error && answer.starts_with("Not switched"), "{message:?}: {answer}");
    }
    let (answer, is_error) = session.call("start_data_mode", json!({ "message": "!exit" })).await;
    assert!(is_error && answer.contains("asks for estate mode") && answer.contains("end_data_mode"), "{answer}");
    assert_eq!(mode_of(&mut session).await["mode"], json!("estate"));

    // The person's `!data` moves it, and the answer carries the question after the command.
    let (started, is_error) =
        session.call("start_data_mode", json!({ "message": "!data which suppliers ship to norway" })).await;
    let started: Value = serde_json::from_str(&started).expect("json");
    assert!(!is_error, "{started}");
    assert_eq!(started, json!({ "mode": "data", "switched": true, "question": "which suppliers ship to norway" }));
    let mode = mode_of(&mut session).await;
    assert_eq!(mode["mode"], json!("data"), "{mode}");
    assert!(mode["setBy"].as_str().unwrap_or_default().contains("person's last command"), "{mode}");

    // In data mode the estate's tools, a module's among them, are refused, and the data tools run.
    for tool in ["list_probes", "list_runs", "search_all"] {
        let (refused, is_error) = session.call(tool, json!({})).await;
        assert!(is_error && is_mode_refusal(&refused) && refused.contains("end_data_mode") && refused.contains("`!exit`"), "{tool}: {refused}");
    }
    let (ran, is_error) = session.call("find_similar_questions", json!({ "question": "which regions" })).await;
    assert!(!is_mode_refusal(&ran) && !is_error, "{ran}");

    // `!data` again is no switch, and a follow-up is no exit.
    let (again, is_error) = session.call("start_data_mode", json!({ "message": "!data and by region?" })).await;
    assert!(!is_error && serde_json::from_str::<Value>(&again).expect("json")["switched"] == json!(false), "{again}");
    let (answer, is_error) = session.call("end_data_mode", json!({ "message": "which flow loads that table?" })).await;
    assert!(is_error && answer.starts_with("Not switched"), "{answer}");
    assert_eq!(mode_of(&mut session).await["mode"], json!("data"));

    // `!exit` returns, with the question after it, and the estate's tools run again.
    let (ended, is_error) = session.call("end_data_mode", json!({ "message": "!exit which flows load it?" })).await;
    assert!(!is_error, "{ended}");
    assert_eq!(serde_json::from_str::<Value>(&ended).expect("json"), json!({ "mode": "estate", "switched": true, "question": "which flows load it?" }));
    let (probes, is_error) = session.call("list_probes", json!({})).await;
    assert!(!is_error && probes.contains("wells a"), "{probes}");
    let (refused, is_error) = session.call("run_query", json!({ "planId": "p-1" })).await;
    assert!(is_error && is_mode_refusal(&refused), "{refused}");

    // The list stayed what it was, and nothing announced a change.
    assert_eq!(tool_names(&session.request("tools/list", json!({})).await), names);
    assert!(!session.notifications.iter().any(|n| n.contains("list_changed")), "{:?}", session.notifications);
}

/// Every message of the table both readers are held to, relayed to both switches in turn through a session that names no
/// mode, over each transport: a switch happens exactly when the table says the message carries that mode's command, with
/// the question the table gives, and the session answers tools in the mode it is then in.
#[tokio::test(flavor = "multi_thread")]
async fn every_message_in_the_shared_table_moves_a_switching_session_as_the_table_says() {
    let estate = start_control_plane().await;
    let stdio = probe_host()
        .server(control_plane(&estate, Some("stored-sign-in")), "", false)
        .expect("the probe host composes");
    let (mut line, _) = open_stdio(stdio).await;
    let walked = walk_the_command_table(&mut line).await;

    let http = probe_host().server(control_plane(&estate, None), "", true).expect("the probe host composes");
    let (url, _stop) = start_http(http).await;
    let (mut remote, _) = HttpSession::open(&url, "caller-a").await;
    assert_eq!(walk_the_command_table(&mut remote).await, walked);
    assert!(walked >= 50, "{walked} messages walked");
}

/// Relays each message of `tests/assistant-commands.json` to start_data_mode and end_data_mode, checking each answer, the
/// mode after it and the gate against it; the number of messages walked.
async fn walk_the_command_table(session: &mut impl Caller) -> usize {
    let table: Value = serde_json::from_str(include_str!("assistant-commands.json")).expect("the table is JSON");
    let cases = table["cases"].as_array().expect("the table has cases");
    let mut mode = "estate";
    for case in cases {
        let message = case["message"].as_str().expect("a message");
        for (tool, target) in [("start_data_mode", "data"), ("end_data_mode", "estate")] {
            let (answer, is_error) = session.tool(tool, json!({ "message": message })).await;
            if case["mode"] == json!(target) {
                assert!(!is_error, "{tool} {message:?}: {answer}");
                let answer: Value = serde_json::from_str(&answer).expect("a switch answers JSON");
                assert_eq!(answer["mode"], json!(target), "{message:?}");
                assert_eq!(answer["switched"], json!(mode != target), "{message:?}");
                assert_eq!(answer["question"], case["question"], "{message:?}");
                mode = target;
            } else {
                assert!(is_error && answer.starts_with("Not switched"), "{tool} {message:?}: {answer}");
            }
            assert_eq!(mode_of(session).await["mode"], json!(mode), "after {tool} {message:?}");
        }
        let (probes, is_error) = session.tool("list_probes", json!({})).await;
        assert_eq!(is_error && is_mode_refusal(&probes), mode == "data", "list_probes in {mode} mode: {probes}");
    }
    cases.len()
}

/// Every tool a session that names no mode lists, in each mode: the server refuses each tool the mode does not answer,
/// before it runs, and runs the ones it does. The refused set is the one `modes::answers_in` defines, so the gate and the
/// rule cannot drift apart.
#[tokio::test(flavor = "multi_thread")]
async fn a_switching_session_refuses_every_tool_of_the_other_mode() {
    let estate = start_control_plane().await;
    let server = probe_host()
        .server(control_plane(&estate, Some("stored-sign-in")), "", false)
        .expect("the probe host composes");
    let (mut session, _) = open_stdio(server).await;
    let catalogue = tool_names(&session.request("tools/list", json!({})).await);
    assert!(catalogue.len() > 80, "{} tools", catalogue.len());

    for (command, mode) in [("!exit", Mode::Estate), ("!data", Mode::Data)] {
        let switch = if mode == Mode::Data { "start_data_mode" } else { "end_data_mode" };
        let (answer, is_error) = session.call(switch, json!({ "message": command })).await;
        assert!(!is_error, "{answer}");
        let mut refused = 0;
        for tool in catalogue.iter().filter(|tool| !modes::answers_in(mode, tool)) {
            let (text, is_error) = session.call(tool, json!({})).await;
            assert!(is_error && is_mode_refusal(&text), "{tool} in {} mode ran: {text}", mode.name());
            refused += 1;
        }
        assert!(refused > 0, "{} mode refuses nothing", mode.name());
        assert_eq!(mode_of(&mut session).await["mode"], json!(mode.name()), "refusals do not move the session");
    }

    // The tools each mode answers run: a sample that touches nothing outside the fake control plane.
    for (tool, arguments) in [("describe_semantic_table", json!({ "key": "k" })), ("find_similar_questions", json!({ "question": "q" }))] {
        let (text, _) = session.call(tool, arguments).await;
        assert!(!is_mode_refusal(&text), "{tool}: {text}");
    }
    let (answer, _) = session.call("end_data_mode", json!({ "message": "!estate" })).await;
    assert_eq!(serde_json::from_str::<Value>(&answer).expect("json")["switched"], json!(true), "{answer}");
    for (tool, arguments) in [("list_probes", json!({})), ("list_repos", json!({})), ("describe_semantic_table", json!({ "key": "k" }))] {
        let (text, _) = session.call(tool, arguments).await;
        assert!(!is_mode_refusal(&text), "{tool}: {text}");
    }
}

/// Every tool there is, called in a session its client named, over HTTP: anything outside the mode's list is refused
/// before it runs, whatever the model's client had listed.
#[tokio::test(flavor = "multi_thread")]
async fn a_named_session_refuses_every_tool_outside_its_mode() {
    let estate = start_control_plane().await;
    let server = probe_host().server(control_plane(&estate, None), "", true).expect("the probe host composes");
    let (url, _stop) = start_http(server).await;
    let (mut every, _) = HttpSession::open(&url, "caller-a").await;
    let catalogue = tool_names(&every.request("tools/list", json!({})).await);

    for mode in [Mode::Data, Mode::Estate] {
        let (mut session, _) = HttpSession::open(&format!("{url}?surface=assistant&mode={}", mode.name()), "caller-b").await;
        let listed = tool_names(&session.request("tools/list", json!({})).await);
        let expected: Vec<String> = catalogue.iter().filter(|tool| modes::named_lists(mode, tool)).cloned().collect();
        assert_eq!(listed, expected, "{} mode lists", mode.name());
        let mut refused = 0;
        for tool in catalogue.iter().filter(|tool| !modes::named_lists(mode, tool) && !modes::is_mode_tool(tool)) {
            let (text, is_error) = session.call_checked(tool, json!({})).await;
            assert!(is_error && is_mode_refusal(&text), "{tool} in {} mode: {text}", mode.name());
            refused += 1;
        }
        assert!(refused > 0, "{} mode refuses nothing", mode.name());
        assert_eq!(mode_of(&mut session).await["mode"], json!(mode.name()));
    }
}

/// A switch the model calls with arguments that are not a person's message switches nothing: the call is refused, or
/// answered as not switched, and the session stays where it was. A message of any size or content is read safely.
#[tokio::test(flavor = "multi_thread")]
async fn a_switch_given_anything_but_a_command_switches_nothing() {
    let estate = start_control_plane().await;
    let server = probe_host()
        .server(control_plane(&estate, Some("stored-sign-in")), "", false)
        .expect("the probe host composes");
    let (mut session, _) = open_stdio(server).await;

    for arguments in [json!({}), json!({ "message": 42 }), json!({ "message": null }), json!({ "message": ["!data"] }), json!({ "text": "!data" })] {
        let answer = session.request("tools/call", json!({ "name": "start_data_mode", "arguments": arguments })).await;
        let refused = answer.get("error").is_some() || answer["result"]["isError"] == json!(true);
        assert!(refused, "{arguments}: {answer}");
        assert_eq!(mode_of(&mut session).await["mode"], json!("estate"), "{arguments}");
    }
    for message in ["\u{0}!data which regions", "\"!data\"", "> !data", "！data", "data", "  ", "\u{200B}"] {
        let (answer, is_error) = session.call("start_data_mode", json!({ "message": message })).await;
        assert!(is_error && answer.starts_with("Not switched"), "{message:?}: {answer}");
    }
    assert_eq!(mode_of(&mut session).await["mode"], json!("estate"));

    // A long message is read whole: the question after the command comes back as it was.
    let question = "which customers bought a bike ".repeat(8_000);
    let (answer, is_error) = session.call("start_data_mode", json!({ "message": format!("!data {question}") })).await;
    assert!(!is_error, "{}", &answer[..answer.len().min(200)]);
    let answer: Value = serde_json::from_str(&answer).expect("json");
    assert_eq!(answer["question"], json!(question.trim_end()));
    // Unknown arguments beside the message are ignored; the message decides.
    let (answer, is_error) = session.call("end_data_mode", json!({ "message": "!exit", "mode": "data" })).await;
    assert!(!is_error && serde_json::from_str::<Value>(&answer).expect("json")["mode"] == json!("estate"), "{answer}");
}

/// Many sessions at once, over one HTTP server: named ones in each mode and switching ones moving back and forth, each
/// calling a tool of each mode on every round. Every session answers in its own mode throughout; a switch in one never
/// moves another.
#[tokio::test(flavor = "multi_thread", worker_threads = 8)]
async fn concurrent_sessions_each_keep_their_own_mode() {
    let estate = start_control_plane().await;
    let server = probe_host().server(control_plane(&estate, None), "", true).expect("the probe host composes");
    let (url, _stop) = start_http(server).await;

    let mut sessions = tokio::task::JoinSet::new();
    for index in 0..36_usize {
        let url = url.clone();
        sessions.spawn(async move {
            let kind = index % 3;
            let address = match kind {
                0 => format!("{url}?surface=assistant&mode=data"),
                1 => format!("{url}?surface=assistant&mode=estate"),
                _ => url.clone(),
            };
            let (mut session, _) = HttpSession::open(&address, &format!("caller-{index}")).await;
            for round in 0..10_usize {
                let expected = match kind {
                    0 => Mode::Data,
                    1 => Mode::Estate,
                    _ => {
                        // A switching session moves on every round, offset by its index, so its neighbours disagree.
                        let target = if (round + index) % 2 == 0 { Mode::Data } else { Mode::Estate };
                        let (tool, command) =
                            if target == Mode::Data { ("start_data_mode", "!data q") } else { ("end_data_mode", "!exit") };
                        let (answer, is_error) = session.call_checked(tool, json!({ "message": command })).await;
                        assert!(!is_error, "session {index} round {round}: {answer}");
                        target
                    }
                };
                assert_eq!(mode_of(&mut session).await["mode"], json!(expected.name()), "session {index} round {round}");
                let (probes, is_error) = session.call_checked("list_repos", json!({})).await;
                assert_eq!(is_error && is_mode_refusal(&probes), expected == Mode::Data, "session {index} round {round}: {probes}");
                let (query, is_error) = session.call_checked("run_query", json!({ "planId": format!("p-{index}-{round}") })).await;
                assert_eq!(is_error && is_mode_refusal(&query), expected == Mode::Estate, "session {index} round {round}: {query}");
                tokio::task::yield_now().await;
            }
            index
        });
    }
    let mut finished = 0;
    while let Some(done) = sessions.join_next().await {
        done.expect("a session ran to its end");
        finished += 1;
    }
    assert_eq!(finished, 36);
}

/// A stdio server registered in data mode for an editor (`--mode data`) lists data mode's tools and the setup tools
/// it signs in with, never a switch: its client may not refresh its list, so it stays in its mode, and says which
/// registration answers a question about the estate. Its instructions open with what the registration means.
#[tokio::test(flavor = "multi_thread")]
async fn a_stdio_server_registered_in_data_mode_lists_its_tools_and_stays_in_it() {
    let estate = start_control_plane().await;
    let server = probe_host()
        .server(control_plane(&estate, Some("stored-sign-in")), "", false)
        .expect("the probe host composes")
        .registered_in(Mode::Data)
        .expect("a stdio server registers in a mode");
    let (mut session, initialized) = open_stdio(server).await;

    let instructions = initialized["result"]["instructions"].as_str().expect("instructions");
    assert!(instructions.starts_with("SQLFlow MCP server.\n\nDATA MODE, BEFORE ANYTHING ELSE"), "{instructions}");
    assert!(instructions.contains("login (device flow)") && instructions.contains("`grounding`"), "{instructions}");
    assert!(!instructions.contains("MODES AND ROUTING") && !instructions.contains("run_probe"), "{instructions}");

    let mut listed = tool_names(&session.request("tools/list", json!({})).await);
    listed.sort();
    let mut expected: Vec<String> = SqlFlowMcp::tool_names()
        .into_iter()
        .filter(|name| modes::lists_registered(Mode::Data, name))
        .collect();
    expected.sort();
    assert_eq!(listed, expected);
    for tool in ["find_similar_questions", "prepare_query", "describe_semantic_table", "get_mode", "login", "check_auth_status"] {
        assert!(listed.contains(&tool.to_string()), "{tool}: {listed:?}");
    }
    for tool in ["start_data_mode", "end_data_mode", "list_runs", "list_probes"] {
        assert!(!listed.contains(&tool.to_string()), "{tool}: {listed:?}");
    }

    // An estate tool is refused with the registration that answers it; a switch is answered but never made.
    let (refused, is_error) = session.call("list_runs", json!({})).await;
    assert!(is_error && refused.contains("registered in data mode") && refused.contains("estate registration"), "{refused}");
    let (stay, is_error) = session.call("start_data_mode", json!({ "message": "!data which regions" })).await;
    let stay: Value = serde_json::from_str(&stay).expect("json");
    assert!(!is_error && stay["switched"] == json!(false) && stay["mode"] == json!("data"), "{stay}");
    let (ended, is_error) = session.call("end_data_mode", json!({ "message": "!exit" })).await;
    assert!(is_error && ended.contains("estate registration"), "{ended}");
    let mode = mode_of(&mut session).await;
    assert_eq!(mode["mode"], json!("data"), "{mode}");
    assert_eq!((mode["switches"].clone(), mode["listsEveryTool"].clone()), (json!(false), json!(false)), "{mode}");
    assert!(mode["setBy"].as_str().unwrap_or_default().contains("--mode data"), "{mode}");
    assert!(session.notifications.is_empty(), "{:?}", session.notifications);
}

/// Registered in estate mode, the server lists the estate's tools and a module's, and none that only data mode needs.
#[tokio::test(flavor = "multi_thread")]
async fn a_stdio_server_registered_in_estate_mode_reads_no_rows() {
    let estate = start_control_plane().await;
    let server = probe_host()
        .server(control_plane(&estate, Some("stored-sign-in")), "", false)
        .expect("the probe host composes")
        .registered_in(Mode::Estate)
        .expect("a stdio server registers in a mode");
    let (mut session, initialized) = open_stdio(server).await;

    let instructions = initialized["result"]["instructions"].as_str().expect("instructions");
    assert!(instructions.starts_with("SQLFlow MCP server.\n\nESTATE MODE, BEFORE ANYTHING ELSE"), "{instructions}");
    assert!(instructions.ends_with("run_probe runs one and waits for it."), "{instructions}");
    let listed = tool_names(&session.request("tools/list", json!({})).await);
    for tool in ["list_runs", "list_probes", "login", "get_mode", "describe_semantic_table"] {
        assert!(listed.contains(&tool.to_string()), "{tool}: {listed:?}");
    }
    for tool in ["find_similar_questions", "run_query", "start_data_mode", "end_data_mode"] {
        assert!(!listed.contains(&tool.to_string()), "{tool}: {listed:?}");
    }
    let (refused, is_error) = session.call("run_query", json!({ "planId": "p-1" })).await;
    assert!(is_error && refused.contains("data registration"), "{refused}");
    let (start, is_error) = session.call("start_data_mode", json!({ "message": "!data which regions" })).await;
    assert!(is_error && start.contains("data registration"), "{start}");
    assert_eq!(mode_of(&mut session).await["mode"], json!("estate"));
    for switch in ["start_data_mode", "end_data_mode", "MODES AND ROUTING"] {
        assert!(!instructions.contains(switch), "a registered session is told about {switch}");
    }
}

/// A host that offers no data mode has no mode to register in.
#[tokio::test(flavor = "multi_thread")]
async fn a_host_without_data_mode_refuses_a_registration_in_one() {
    let estate = start_control_plane().await;
    let refused = probe_host()
        .without_tools(["find_similar_questions"])
        .server(control_plane(&estate, Some("stored-sign-in")), "", false)
        .expect("the probe host composes")
        .registered_in(Mode::Data)
        .map(|_| ())
        .expect_err("no data mode, no registration in one");
    assert!(refused.to_string().contains("no data mode"), "{refused}");
}

#[tokio::test(flavor = "multi_thread")]
async fn a_host_introduces_its_product_and_does_not_recommend_a_tool_it_leaves_out() {
    let estate = start_control_plane().await;
    let server = probe_host()
        .introduced_as("Probe MCP server. Probes extend SQLFlow; the PROBES section covers what they add.")
        .without_tools(["discover_source", "find_similar_questions"])
        .server(control_plane(&estate, Some("stored-sign-in")), "", false)
        .expect("the probe host composes");
    let (mut session, initialized) = open_stdio(server).await;

    // Without find_similar_questions there is no data mode: nothing is filtered by mode, and no way out of it is listed.
    let names = tool_names(&session.request("tools/list", json!({})).await);
    assert!(!names.contains(&"end_data_mode".to_string()), "{names:?}");
    assert!(names.contains(&"run_query".to_string()) && names.contains(&"list_runs".to_string()), "{names:?}");
    for (tool, arguments) in [("run_query", json!({ "planId": "p-1" })), ("list_probes", json!({}))] {
        let (text, _) = session.call(tool, arguments).await;
        assert!(!is_mode_refusal(&text), "a host without modes refused {tool}: {text}");
    }
    let refused = session.request("tools/call", json!({ "name": "get_mode", "arguments": {} })).await;
    assert!(refused["error"]["message"].as_str().unwrap_or_default().contains("tool not found"), "{refused}");

    // The host's line opens the instructions in place of SQLFlow's, and SQLFlow's tiers follow it.
    let instructions = initialized["result"]["instructions"].as_str().expect("instructions");
    assert!(
        instructions.starts_with("Probe MCP server. Probes extend SQLFlow; the PROBES section covers what they add. Two tiers of tools:"),
        "{instructions}"
    );
    assert!(!instructions.contains("SQLFlow MCP server."), "{instructions}");

    // The paragraph recommending source discovery and the rule routing `!` to find_similar_questions are gone; the
    // closing note still names both tools as not offered.
    assert!(!instructions.contains("Source discovery"), "{instructions}");
    assert!(!instructions.contains("ROUTING, BEFORE ANYTHING ELSE"), "{instructions}");
    assert!(instructions.trim_end().ends_with("say so when a question needs one."), "{instructions}");
    assert!(
        instructions.contains(
            "Not offered by this server, although other tools' descriptions may name them: discover_source, find_similar_questions."
        ),
        "{instructions}"
    );
}

#[test]
fn a_blank_introduction_is_refused() {
    let refused = probe_host().introduced_as("   ").identity().expect_err("a blank introduction");
    assert!(refused.to_string().contains("introduction is blank"), "{refused}");
}

#[test]
fn the_probe_host_keeps_its_sign_in_apart_from_sqlflows() {
    let probe = probe_host().identity().expect("the probe host is well named");
    let sqlflow = McpHost::sqlflow().identity().expect("sqlflow's own host is well named");
    assert_eq!(probe.name(), "probe-mcp");
    assert_eq!(sqlflow.name(), "sqlflow-mcp");
    assert_eq!(probe.state(), &StateStore::new("probe-mcp-test").unwrap());
    assert_eq!(sqlflow.state(), &StateStore::default());
}
