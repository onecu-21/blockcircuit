using BlockCircuit;

if(args.Contains("--self-test")){SelfTests.Run();return;}
if(args.Contains("--engine-tests")){EngineTests.Run();return;}
if(args.Contains("--gameplay-tests")){GameplayTests.Run();return;}
if(args.Contains("--terrain-tests")){TerrainTests.Run();WorldSessionTests.Run();return;}
if(args.Contains("--controls-tests")){ControlsTests.Run();SneakTests.Run();CameraTests.Run();return;}
if(args.Contains("--physics-benchmark")){PhysicsBenchmark.Run();return;}
if(args.Contains("--benchmark")){EngineBenchmark.Run();return;}
if(args.Contains("--circuit-benchmark")){CircuitBenchmark.Run();return;}
bool smoke=args.Any(a=>a.EndsWith("smoke-test"));
bool terrainPreview=args.Contains("--terrain-smoke-test");
var game=new Game(smoke,terrainPreview);
game.Run(smoke,args.Contains("--palette-smoke-test"),args.Contains("--showcase-smoke-test"),
    args.Contains("--engine-smoke-test"),args.Contains("--gameplay-smoke-test"),terrainPreview,args.Contains("--controls-smoke-test"));
