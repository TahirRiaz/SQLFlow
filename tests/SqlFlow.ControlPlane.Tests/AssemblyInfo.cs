using Xunit;

// The control-plane tests share one resource: the catalog database (and, through it, the durable run queue). Every
// WebApplicationFactory also starts the background run worker, which drains that shared queue. Running the classes in
// parallel would let one test's worker claim another test's queued run, so the queue lifecycle assertions ("the run
// I enqueued is the one I claim") would be non-deterministic. Serializing the assembly makes the shared queue a
// single-producer, single-consumer resource per test, which is what those assertions rely on.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
