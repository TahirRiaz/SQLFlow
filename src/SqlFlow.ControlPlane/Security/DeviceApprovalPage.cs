using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace SqlFlow.ControlPlane.Security;

/// <summary>
/// Serves the self-contained device-approval page at <c>GET /device</c>, the <c>verificationUri</c> advertised by
/// the device-authorization grant. It signs the user in against <c>/api/v1/auth/login</c> and then calls
/// <c>/api/v1/auth/device/approve</c> with the entered code, so a human can approve a headless client (the MCP
/// server) without any separate front-end deployment. The page is inlined and has no external dependencies.
/// </summary>
public static class DeviceApprovalPage
{
    public static IEndpointRouteBuilder MapDeviceApprovalPage(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/device", () => Results.Content(Html, "text/html; charset=utf-8"))
            .AllowAnonymous()
            .WithName("DeviceApprovalPage")
            .DisableRateLimiting();
        return app;
    }

    private const string Html = """
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>SQLFlow · Approve device</title>
  <style>
    :root { color-scheme: light dark; }
    body { font-family: system-ui, -apple-system, Segoe UI, Roboto, sans-serif; margin: 0;
           display: grid; place-items: center; min-height: 100vh; background: #0f172a; color: #e2e8f0; }
    .card { width: min(92vw, 420px); background: #1e293b; border: 1px solid #334155;
            border-radius: 12px; padding: 28px; box-shadow: 0 10px 30px rgba(0,0,0,.35); }
    h1 { font-size: 1.25rem; margin: 0 0 4px; }
    p.sub { margin: 0 0 20px; color: #94a3b8; font-size: .9rem; }
    label { display: block; font-size: .8rem; color: #cbd5e1; margin: 14px 0 4px; }
    input { width: 100%; box-sizing: border-box; padding: 10px 12px; border-radius: 8px;
            border: 1px solid #475569; background: #0f172a; color: #e2e8f0; font-size: 1rem; }
    input[readonly] { color: #93c5fd; letter-spacing: .12em; font-weight: 600; }
    button { margin-top: 20px; width: 100%; padding: 11px; border: 0; border-radius: 8px; cursor: pointer;
             background: #2563eb; color: white; font-size: 1rem; font-weight: 600; }
    button:disabled { opacity: .6; cursor: default; }
    .msg { margin-top: 16px; font-size: .9rem; min-height: 1.2em; }
    .ok { color: #4ade80; } .err { color: #f87171; }
  </style>
</head>
<body>
  <div class="card">
    <h1>Approve device sign-in</h1>
    <p class="sub">Sign in to authorize a SQLFlow client (such as the MCP server) to act with your read/operate access.</p>
    <form id="f">
      <label for="code">Device code</label>
      <input id="code" name="code" required autocomplete="off" />
      <label for="user">Username</label>
      <input id="user" name="user" required autocomplete="username" />
      <label for="pass">Password</label>
      <input id="pass" name="pass" type="password" required autocomplete="current-password" />
      <button id="btn" type="submit">Approve</button>
      <div id="msg" class="msg"></div>
    </form>
  </div>
  <script>
    const qs = new URLSearchParams(location.search);
    const codeEl = document.getElementById('code');
    if (qs.get('code')) { codeEl.value = qs.get('code'); }
    const msg = document.getElementById('msg');
    const btn = document.getElementById('btn');
    document.getElementById('f').addEventListener('submit', async (e) => {
      e.preventDefault();
      msg.className = 'msg'; msg.textContent = 'Signing in…'; btn.disabled = true;
      try {
        const login = await fetch('/api/v1/auth/login', {
          method: 'POST', headers: { 'content-type': 'application/json' },
          body: JSON.stringify({ username: document.getElementById('user').value, password: document.getElementById('pass').value })
        });
        if (!login.ok) { throw new Error('Sign-in failed (' + login.status + ')'); }
        const session = await login.json();
        const approve = await fetch('/api/v1/auth/device/approve', {
          method: 'POST',
          headers: { 'content-type': 'application/json', 'authorization': 'Bearer ' + session.token },
          body: JSON.stringify({ userCode: codeEl.value })
        });
        if (approve.status === 204) {
          msg.className = 'msg ok'; msg.textContent = 'Approved. You can close this window and return to your client.';
        } else {
          const body = await approve.json().catch(() => ({}));
          throw new Error(body.title || ('Approval failed (' + approve.status + ')'));
        }
      } catch (err) {
        msg.className = 'msg err'; msg.textContent = err.message || 'Something went wrong.'; btn.disabled = false;
      }
    });
  </script>
</body>
</html>
""";
}
