# Windows mixer persistence probe

This standalone probe compiles the production Core Audio helper directly from the Peek sources. It is kept on the fork's validation branch and is not part of the upstream pull request.

Run on Windows with a working default audio output and .NET 10:

```powershell
cd validation/volume-probe
dotnet build VolumeProbe.csproj -p:ImportDirectoryBuildProps=false -p:ImportDirectoryBuildTargets=false -p:ManagePackageVersionsCentrally=false -p:IsAotCompatible=true -p:AnalysisMode=Recommended -p:TreatWarningsAsErrors=true --source https://api.nuget.org/v3/index.json
dotnet bin/Debug/net10.0-windows10.0.26100.0/VolumeProbe.dll save
dotnet bin/Debug/net10.0-windows10.0.26100.0/VolumeProbe.dll restore
```

The build enables AOT compatibility analysis and the repository's StyleCop settings. The first process restores a 20% session level, changes the Windows mixer level to 35%, verifies persistence and subsequent player sessions, and checks notification cleanup. The second process loads the saved 35% level and verifies restoration after a process restart. The separate player gain stays at 40%.

The probe uses muted players and a generated silent WAV file. It changes only audio sessions belonging to its own process and writes test data under its build output directory. Peek settings serialization is covered separately by `CommonLibTest.PeekSettingsTests` in the full-project validation workflow.
