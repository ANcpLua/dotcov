using System.Reflection;
using System.Reflection.Emit;
using DotCov.CoverageHook;
using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;

[TestClass]
[ResourceLock(ProcessState.Environment)]
public sealed class CoverageFlusherTests(TestContext testContext)
{
    const string SessionVariable = "CODE_COVERAGE_SESSION_NAME";
    static int _calls;

    [TestMethod]
    public void WithoutAnActiveCoverageSession_DoesNothing()
    {
        using var environment = EnvScope.Clear(SessionVariable);
        using var diagnostics = new StringWriter();
        CoverageFlusher.Flush(diagnostics);
        Assert.AreEqual("", diagnostics.ToString());
    }

    [TestMethod]
    public void FlushesEveryLoadedTrackerForTheActiveSession_WithoutTouchingAnotherSession()
    {
        var session = Guid.NewGuid().ToString();
        using var environment = new EnvScope((SessionVariable, session));
        AddTracker(session);
        AddTracker(session);
        AddTracker(Guid.NewGuid().ToString());
        _calls = 0;
        using var diagnostics = new StringWriter();
        CoverageFlusher.Flush(diagnostics);
        Assert.AreEqual(2, _calls);
        Assert.AreEqual("", diagnostics.ToString());
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("argument")]
    [DataRow("return")]
    [DataRow("private")]
    public void IgnoresTypesWithoutAPublicStaticParameterlessVoidFlush(string shape)
    {
        var session = Guid.NewGuid().ToString();
        using var environment = new EnvScope((SessionVariable, session));
        AddTracker(session, shape);
        _calls = 0;
        using var diagnostics = new StringWriter();
        CoverageFlusher.Flush(diagnostics);
        Assert.AreEqual(0, _calls);
        Assert.AreEqual("", diagnostics.ToString());
    }

    [TestMethod]
    public void ReportsAFlushFailure_AndContinuesWithoutThrowing()
    {
        var session = Guid.NewGuid().ToString();
        using var environment = new EnvScope((SessionVariable, session));
        AddTracker(session, "throw");
        AddTracker(session, "throw");
        _calls = 0;
        using var diagnostics = new StringWriter();
        CoverageFlusher.Flush(diagnostics);
        Assert.AreEqual(2, _calls);
        Assert.AreEqual(2, diagnostics.ToString().Split("flush sentinel").Length - 1);
        Assert.Contains("dotcov: coverage flush failed for", diagnostics.ToString());
    }

    [TestMethod]
    public void StopsAndJoinsPeriodicPublicationBeforeTheFinalFlush()
    {
        var session = Guid.NewGuid().ToString();
        using var environment = new EnvScope((SessionVariable, session));
        using var ready = new ManualResetEventSlim();
        var worker = new Thread(static state =>
        {
            ((ManualResetEventSlim)state!).Set();
            try { Thread.Sleep(Timeout.Infinite); }
            catch (ThreadInterruptedException) { }
        }) { IsBackground = true };
        worker.Start(ready);
        try
        {
            ready.Wait(testContext.CancellationToken);
            AddTracker(session, worker: worker, threadField: true);
            _calls = 0;
            using var diagnostics = new StringWriter();
            CoverageFlusher.Flush(diagnostics);
            Assert.IsFalse(worker.IsAlive);
            Assert.AreEqual(1, _calls);
            Assert.AreEqual("", diagnostics.ToString());
        }
        finally
        {
            if (worker.IsAlive) worker.Interrupt();
            Assert.IsTrue(worker.Join(5000));
        }
    }

    [TestMethod]
    [DataRow("null")]
    [DataRow("stopped")]
    [DataRow("current")]
    public void FlushesWithoutJoiningAnAbsentStoppedOrCurrentWorker(string state)
    {
        var session = Guid.NewGuid().ToString();
        using var environment = new EnvScope((SessionVariable, session));
        Thread? worker = state is "current" ? Thread.CurrentThread : null;
        if (state is "stopped")
        {
            worker = new Thread(static () => { });
            worker.Start();
            worker.Join();
        }
        AddTracker(session, worker: worker, threadField: true);
        _calls = 0;
        using var diagnostics = new StringWriter();
        CoverageFlusher.Flush(diagnostics);
        Assert.AreEqual(1, _calls);
        Assert.AreEqual("", diagnostics.ToString());
    }

    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public void ReportsABlockedPublicationWithoutAnUnboundedWaitOrConcurrentFlush()
    {
        var session = Guid.NewGuid().ToString();
        using var environment = new EnvScope((SessionVariable, session));
        using var ready = new ManualResetEventSlim();
        using var stop = new ManualResetEventSlim();
        var worker = new Thread(static state =>
        {
            var work = ((ManualResetEventSlim Ready, ManualResetEventSlim Stop, CancellationToken Cancellation))state!;
            work.Ready.Set();
            while (!work.Stop.IsSet && !work.Cancellation.IsCancellationRequested)
                Thread.SpinWait(100);
        }) { IsBackground = true };
        worker.Start((ready, stop, testContext.CancellationToken));
        try
        {
            ready.Wait(testContext.CancellationToken);
            AddTracker(session, worker: worker, threadField: true);
            _calls = 0;
            using var diagnostics = new StringWriter();
            CoverageFlusher.Flush(diagnostics);
            Assert.AreEqual(0, _calls);
            Assert.Contains("coverage publication thread did not stop within one second", diagnostics.ToString());
        }
        finally
        {
            stop.Set();
            Assert.IsTrue(worker.Join(5000));
        }
    }

    [TestMethod]
    public void StartupHookCanBeInitializedMoreThanOnce()
    {
        StartupHook.Initialize();
        StartupHook.Initialize();
    }

    // The injected tracker contract, emitted into separate loaded assemblies. The process
    // regression also exercises the actual Microsoft tracker and real fatal termination.
    static void AddTracker(string session, string shape = "normal", Thread? worker = null, bool threadField = false)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("TrackerTest" + Guid.NewGuid().ToString("N")), AssemblyBuilderAccess.Run);
        var module = assembly.DefineDynamicModule("TrackerTest");
        var type = module.DefineType("Microsoft.CodeCoverage.Instrumentation.Static.Tracker.StaticManagedTrackerTemplate_" + session,
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        if (threadField)
            type.DefineField("_flushThread", typeof(Thread), FieldAttributes.Private | FieldAttributes.Static);
        if (shape is not "missing")
        {
            var method = type.DefineMethod("Flush", MethodAttributes.Static | (shape is "private" ? MethodAttributes.Private : MethodAttributes.Public),
                shape is "return" ? typeof(int) : typeof(void), shape is "argument" ? [typeof(int)] : Type.EmptyTypes);
            var body = method.GetILGenerator();
            body.Emit(OpCodes.Call, typeof(CoverageFlusherTests).GetMethod(nameof(RecordFlush))!);
            if (shape is "throw")
            {
                body.Emit(OpCodes.Ldstr, "flush sentinel");
                body.Emit(OpCodes.Newobj, typeof(IOException).GetConstructor([typeof(string)])!);
                body.Emit(OpCodes.Throw);
            }
            if (shape is "return") body.Emit(OpCodes.Ldc_I4_0);
            body.Emit(OpCodes.Ret);
        }
        var created = type.CreateType();
        if (threadField)
            created.GetField("_flushThread", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, worker);
    }

    public static void RecordFlush()
    {
        _calls++;
    }
}
