using BlockCircuit;

if(args.Contains("--self-test")){SelfTests.Run();return;}
if(args.Contains("--engine-tests")){EngineTests.Run();return;}
if(args.Contains("--gameplay-tests")){GameplayTests.Run();return;}
if(args.Contains("--benchmark")){EngineBenchmark.Run();return;}
if(args.Contains("--circuit-benchmark")){CircuitBenchmark.Run();return;}
bool smoke=args.Any(a=>a.EndsWith("smoke-test"));
var game=new Game(smoke);
game.Run(smoke,args.Contains("--palette-smoke-test"),args.Contains("--showcase-smoke-test"),
    args.Contains("--engine-smoke-test"),args.Contains("--gameplay-smoke-test"));
