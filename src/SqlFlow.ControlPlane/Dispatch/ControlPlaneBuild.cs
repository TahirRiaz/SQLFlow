using System.Security.Cryptography;
using System.Text;
using SqlFlow.Dispatch.ManagedWorkers;

namespace SqlFlow.ControlPlane.Dispatch;

/// <summary>
/// This control plane's build, as the four hex digits managed workers carry in their names (<see cref="WorkerNames"/>),
/// so two processes of the same deployment agree (a failover keeps the fleet) and a different one disagrees (a redeploy
/// replaces it). Made from the deployment's own build when the launcher names one
/// (<see cref="IWorkerLauncher.DeploymentBuild"/>, the commit every component was built from); otherwise from a SHA-256
/// over the name and bytes of every <c>SqlFlow*.dll</c> beside the host. Builds are deterministic, so the same sources
/// give the same bytes; a rebuild of the same sources that differs only replaces workers it did not need to, which is
/// safe. Two builds share a tag one time in 65,536, which at worst keeps the old build's workers until their lifetime.
/// </summary>
public static class ControlPlaneBuild
{
    /// <summary>The longest deployment build text taken into the tag, and shown in the log.</summary>
    private const int MaxDeploymentBuildLength = 200;

    /// <summary>The tag, where it came from (for the log), and whether the build could be read at all: the deployment's
    /// build when <paramref name="deploymentBuild"/> names one, else the assemblies in <paramref name="directory"/>. An
    /// unreadable build gives <see cref="WorkerNames.UnknownBuild"/>, which every other unreadable build shares, so such
    /// control planes cannot tell their workers apart.</summary>
    public static (string Tag, string Source, bool Known) Resolve(string? deploymentBuild, string directory)
    {
        var build = deploymentBuild?.Trim();
        if (!string.IsNullOrEmpty(build))
        {
            if (build.Length > MaxDeploymentBuildLength)
            {
                build = build[..MaxDeploymentBuildLength];
            }

            return (TagOf(build), $"the deployment's build '{build}'", true);
        }

        var tag = Tag(directory);
        return (tag, $"the control plane's assemblies in {directory}", tag != WorkerNames.UnknownBuild);
    }

    /// <summary>The tag of a deployment's build text.</summary>
    public static string TagOf(string deploymentBuild)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentBuild);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(deploymentBuild.Trim())))[..4];
    }

    /// <summary>The tag of the assemblies in <paramref name="directory"/>; <see cref="WorkerNames.UnknownBuild"/> when there
    /// are none or they cannot be read.</summary>
    public static string Tag(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        try
        {
            var files = Directory.EnumerateFiles(directory, "SqlFlow*.dll", SearchOption.TopDirectoryOnly)
                .Order(StringComparer.Ordinal)
                .ToList();
            if (files.Count == 0)
            {
                return WorkerNames.UnknownBuild;
            }

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            foreach (var file in files)
            {
                hash.AppendData(Encoding.UTF8.GetBytes(Path.GetFileName(file)));
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                }
            }

            return Convert.ToHexStringLower(hash.GetHashAndReset())[..4];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return WorkerNames.UnknownBuild;
        }
    }
}
