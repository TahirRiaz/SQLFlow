using SqlFlow.SourceControl.Proposals;

namespace SqlFlow.ControlPlane.Proposals;

/// <summary>
/// Binds the proposal staging area to the control-plane process lifetime. A proposal already stages its git clone in
/// a throwaway directory it deletes when the request finishes, so nothing is meant to survive a call; this sweep is
/// the backstop for the one case that escapes a per-call <c>finally</c>: a hard process kill mid-publish. It clears
/// the whole work root on startup (reclaiming any crash leak from a previous process before this one stages
/// anything) and again on shutdown (so a session leaves nothing behind). The root is per-host temp storage, so each
/// replica sweeps only its own staging area.
/// </summary>
public sealed class ProposalWorkspaceJanitor : IHostedService
{
    private readonly ILogger<ProposalWorkspaceJanitor> _logger;

    public ProposalWorkspaceJanitor(ILogger<ProposalWorkspaceJanitor> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Sweep("startup");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Sweep("shutdown");
        return Task.CompletedTask;
    }

    private void Sweep(string phase)
    {
        try
        {
            GitProposalPublisher.ClearWorkRoot();
            _logger.LogInformation("Cleared the proposal staging area on {Phase} ({Root}).", phase, GitProposalPublisher.DefaultWorkRoot);
        }
        catch (Exception ex)
        {
            // Best-effort: a staging area we could not clear (a locked file, for example) is harmless temp data the
            // next startup sweep reclaims. Never let cleanup fail process start or stop.
            _logger.LogWarning(ex, "Could not fully clear the proposal staging area on {Phase} ({Root}).", phase, GitProposalPublisher.DefaultWorkRoot);
        }
    }
}
