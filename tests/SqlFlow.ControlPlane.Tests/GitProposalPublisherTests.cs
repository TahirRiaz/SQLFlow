using SqlFlow.SourceControl.Proposals;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>The proposal staging-area cleanup that binds staged git clones to the control-plane process lifetime:
/// it must remove a staged clone including the read-only objects a git working tree keeps under <c>.git</c>, and be
/// a harmless no-op when the root does not exist.</summary>
public sealed class GitProposalPublisherTests
{
    [Fact]
    public void ClearWorkRoot_RemovesStagedClonesIncludingReadOnlyFiles()
    {
        var root = NewTempRoot();
        var gitDir = Path.Combine(root, "proposal-clone", ".git");
        Directory.CreateDirectory(gitDir);
        var packedObject = Path.Combine(gitDir, "packed-refs");
        File.WriteAllText(packedObject, "ref");
        File.SetAttributes(packedObject, FileAttributes.ReadOnly);

        GitProposalPublisher.ClearWorkRoot(root);

        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void ClearWorkRoot_OnMissingRoot_DoesNotThrow()
    {
        var root = NewTempRoot();

        // Never created; the sweep must tolerate an absent root (a process that opened no proposals this session).
        GitProposalPublisher.ClearWorkRoot(root);

        Assert.False(Directory.Exists(root));
    }

    private static string NewTempRoot()
        => Path.Combine(Path.GetTempPath(), "sqlflow_proposal_test_" + Guid.NewGuid().ToString("N"));
}
