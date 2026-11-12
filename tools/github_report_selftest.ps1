# Pure URL/privacy contract tests. Never opens a browser, reads user diagnostics, or submits an issue.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$work = Join-Path ([IO.Path]::GetTempPath()) ('Streamsthetic-github-report-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$helper = [Security.SecurityElement]::Escape((Join-Path $root 'windows/app/GitHubIssueDraft.cs'))
@"
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup><Compile Include="$helper" Link="GitHubIssueDraft.cs" /></ItemGroup></Project>
"@ | Set-Content -LiteralPath (Join-Path $work 'ReportTest.csproj') -Encoding UTF8
@'
using System.Runtime.InteropServices;
using PhoneCam;
int count = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS: " + name); count++; }
var from = new DateTime(2026, 11, 12, 10, 0, 0, DateTimeKind.Utc);
var to = from.AddMinutes(15);
GitHubIssueDraft Make(string text, string? kind = null) => GitHubIssueDraft.Create(text, new Version(0, 2, 4), new Version(10, 0, 26100), Architecture.X64, from, to, kind);
Dictionary<string, string> Query(GitHubIssueDraft draft) => draft.Url.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
var text = "Picture flickers? 日本語 😀 &body=overwrite #fragment\nSecond line + 50% / \\\"quoted\\\"";
var normal = Make(text, "receiver-crash");
var query = Query(normal);
Check(normal.Url.Scheme == "https" && normal.Url.Host == "github.com" && normal.Url.AbsolutePath == "/cyclessomething/streamsthetic/issues/new", "only the intended HTTPS issue draft destination");
Check(query.Count == 2 && query.ContainsKey("title") && query.ContainsKey("body") && normal.Url.Fragment == "", "description cannot inject URL parameters or fragments");
Check(query["body"].Contains(text), "Unicode, punctuation and line breaks round-trip exactly");
Check(query["body"].Contains("0.2.4") && query["body"].Contains("10.0.26100") && query["body"].Contains("x64") && query["body"].Contains("2026-11-12 10:00:00") && query["body"].Contains("Receiver crash"), "explicit version OS architecture time and known category included");
var sensitiveContext = "pairing=123456 ip=192.168.2.3 serial=PRIVATE_DEVICE rawlog=SECRET_LOG dump=C:\\private\\crash.dmp";
var excluded = Query(Make("User supplied description", sensitiveContext))["body"];
Check(!new[] { "123456", "192.168.2.3", "PRIVATE_DEVICE", "SECRET_LOG", "crash.dmp" }.Any(excluded.Contains), "untrusted diagnostic category cannot leak secret fixtures");
Check(excluded.Contains("Other / not specified"), "unknown category maps to a fixed safe label");
Check(Query(Make(sensitiveContext))["body"].Contains(sensitiveContext), "explicit user description is preserved rather than silently filtered");
Check(!normal.DescriptionShortened && Query(Make(""))["body"].Contains("Describe what happened"), "short and empty descriptions have sensible drafts");
var longText = string.Concat(Enumerable.Repeat("長い説明😀&?#\n", 4000));
var longDraft = Make(longText);
Check(longDraft.DescriptionShortened && longDraft.Url.AbsoluteUri.Length <= GitHubIssueDraft.MaxUrlLength, "long encoded URLs are bounded");
Check(longDraft.FullBody.Contains(longText), "full description remains available for local preservation");
var body = Query(longDraft)["body"];
Check(body.Contains("Description shortened") && !body.Contains('\uFFFD'), "shortening is explicit and preserves Unicode scalar boundaries");
Check(body.Contains("Windows version: 10.0.26100"), "long description cannot displace diagnostic summary");
Console.WriteLine($"GITHUB REPORT TESTS PASS ({count})");
'@ | Set-Content -LiteralPath (Join-Path $work 'Program.cs') -Encoding UTF8
& dotnet run --project (Join-Path $work 'ReportTest.csproj') -c Release --nologo
if ($LASTEXITCODE) { throw "GitHub report helper tests failed; files retained at $work" }
Write-Output "Test source retained at $work"
