namespace SqlFlow.ControlPlane.Hosting;

/// <summary>
/// The authorization policies of the control plane's <c>/api/v1</c> surface. Read, operate and author all require a signed-in
/// user (a signed-in user is never stuck unable to use a feature the GUI shows them); admin additionally requires the
/// <c>admin</c> scope.
/// </summary>
public static class ControlPlanePolicies
{
    /// <summary>Reading the estate, runs and everything else the GUI shows.</summary>
    public const string Read = "read";

    /// <summary>Triggering and cancelling runs, managing schedules, sources and pools.</summary>
    public const string Operate = "operate";

    /// <summary>Authoring changes that go through review, such as pull-request proposals.</summary>
    public const string Author = "author";

    /// <summary>User and role administration.</summary>
    public const string Admin = "admin";
}
