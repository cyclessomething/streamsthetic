$ErrorActionPreference = 'Stop'
$androidRoot = Split-Path $PSScriptRoot -Parent
$outputDir = Join-Path ([IO.Path]::GetTempPath()) ('streamsthetic-android-checks-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $outputDir | Out-Null
& javac -d $outputDir `
    (Join-Path $androidRoot 'app/src/main/java/com/phonecam/OrientationDebouncer.java') `
    (Join-Path $androidRoot 'app/src/main/java/com/phonecam/SavedChoice.java') `
    (Join-Path $PSScriptRoot 'BehaviorChecks.java')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& java -cp $outputDir com.phonecam.BehaviorChecks
exit $LASTEXITCODE
