# Test-edition isolation checks

```sh
dotnet run --project tests/SolarisTestEdition.Tests -c Release
dotnet run --project tests/SolarisTestEdition.Tests -c Release -p:SolarisTestBuild=false
python3 tests/check_solaris_test_isolation.py
```

The console suite links the actual `SolarisTestEdition.cs`. It checks argument
denial, protected-root/prefix boundaries, junction/symlink refusal, isolated
state/game/cache/config paths, mutex separation, and fail-closed initialization.
It uses only newly owned fixtures, never a real Solaris user profile. Test state
is created under the test executable's ignored `bin/.../test-data` directory.

The second compile verifies that a normal binary cannot enable TEST behavior
through runtime arguments. The source guard compares production projections and
exact updater/installer files against the control commit. These checks do not
replace the separate native Windows WPF smoke or interactive Minecraft tests.
