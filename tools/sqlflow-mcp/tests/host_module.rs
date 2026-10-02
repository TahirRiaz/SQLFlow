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
/// caller's bearer, and the session id the server issued at `initialize`.
struct HttpSession {
    http: reqwest::Client,
    url: String,
    bearer: String,
    session: Option<String>,
    next_id: u64,
}

impl HttpSession {
    async fn open(url: &str, bearer: &str) -> (HttpSession, Value) {
        let mut session = HttpSession {
            http: reqwest::Client::new(),
            url: url.to_string(),
            bearer: bearer.to_string(),
            session: None,
            next_id: 0,
        };
        let initialized = session
            .request(
                "initialize",
                json!({
                    "protocolVersion": "2025-03-26",
                    "capabilities": {},
                    "clientInfo": { "name": "host-module-test", "version": "0" },
                }),
            )
            .await;
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

    // Both sets of tools are listed, and SQLFlow's are all still there.
    let names = tool_names(&session.request("tools/list", json!({})).await);
    for tool in ["list_probes", "run_probe", "list_repos", "search_docs", "validate_flow", "trigger_run"] {
        assert!(names.contains(&tool.to_string()), "{tool} is not listed: {names:?}");
    }
    assert_eq!(names.len(), SqlFlowMcp::tool_names().len() + 2);
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

// ---- The server over stdio --------------------------------------------------------------------------

/// A client's end of a stdio session: one JSON-RPC message per line, each way.
struct LineSession {
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
                }
            }
        })
        .await;
        answer.unwrap_or_else(|_| panic!("no answer to {method} within 30s"))
    }
}

#[tokio::test(flavor = "multi_thread")]
async fn over_stdio_a_module_tool_uses_the_servers_own_sign_in() {
    let estate = start_control_plane().await;
    let server = probe_host()
        .server(control_plane(&estate, Some("stored-sign-in")), "", false)
        .expect("the probe host composes");

    let (client_end, server_end) = tokio::io::duplex(1 << 16);
    let (server_read, server_write) = tokio::io::split(server_end);
    tokio::spawn(async move {
        let running = server.serve((server_read, server_write)).await.expect("the session starts");
        let _ = running.waiting().await;
    });
    let (client_read, client_write) = tokio::io::split(client_end);
    let mut session = LineSession { reader: BufReader::new(client_read), writer: client_write, next_id: 0 };

    let initialized = session
        .request(
            "initialize",
            json!({
                "protocolVersion": "2025-03-26",
                "capabilities": {},
                "clientInfo": { "name": "host-module-test", "version": "0" },
            }),
        )
        .await;
    session.send(json!({ "jsonrpc": "2.0", "method": "notifications/initialized" })).await;

    let instructions = initialized["result"]["instructions"].as_str().expect("instructions");
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

#[test]
fn the_probe_host_keeps_its_sign_in_apart_from_sqlflows() {
    let probe = probe_host().identity().expect("the probe host is well named");
    let sqlflow = McpHost::sqlflow().identity().expect("sqlflow's own host is well named");
    assert_eq!(probe.name(), "probe-mcp");
    assert_eq!(sqlflow.name(), "sqlflow-mcp");
    assert_eq!(probe.state(), &StateStore::new("probe-mcp-test").unwrap());
    assert_eq!(sqlflow.state(), &StateStore::default());
}
