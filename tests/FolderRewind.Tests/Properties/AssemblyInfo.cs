using Microsoft.VisualStudio.TestTools.UnitTesting;

// Real archive and filesystem tests share machine I/O capacity even with isolated directories.
// Bound concurrency instead of scaling external processes to every logical processor.
[assembly: Parallelize(Workers = 4, Scope = ExecutionScope.MethodLevel)]
