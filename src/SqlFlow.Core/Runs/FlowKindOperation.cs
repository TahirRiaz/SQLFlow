namespace SqlFlow.Core.Runs;

/// <summary>
/// One operation a run of a registered flow kind can perform, as the kind declares it: the name a trigger, a schedule
/// or the CLI passes (<see cref="RunParameters.Operation"/>), the label and description an operator reads when choosing
/// it, and whether it writes to the flow's target (so a client can ask for confirmation before it does).
/// </summary>
/// <param name="Name">The operation name: lowercase letters, digits and '-', as <see cref="RunParameters.ValidateKindArguments"/> accepts.</param>
/// <param name="Label">A short human label.</param>
/// <param name="Description">One sentence saying what the operation does.</param>
/// <param name="WritesTarget">True when the operation changes the flow's target rather than only reading it.</param>
public sealed record FlowKindOperation(string Name, string Label, string Description, bool WritesTarget);
