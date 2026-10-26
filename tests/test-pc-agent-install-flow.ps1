$ErrorActionPreference = 'Stop'

# Compile the actual UI entry flow with fake dialogs and service operations.
# No real Windows service, enrollment, HTTP request or power command is touched.
$program = Get-Content (Join-Path $PSScriptRoot '../Program.cs') -Raw
$start = $program.IndexOf('    private static int RunPcAgentUi(')
$end = $program.IndexOf('    private static async Task<int> RunAsPcServiceAsync(', $start)
if ($start -lt 0 -or $end -le $start) { throw 'PC Agent UI flow was not found.' }
$methods = $program.Substring($start, $end - $start)
# Use a disposable test-only mutex name so a developer's running UI is untouched.
$methods = $methods.Replace('Global\\VDAKorPcAgent_UI_Mutex', 'VDAKorPcAgent_Test_' + [Guid]::NewGuid().ToString('N'))
$source = @'
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using VdaHubServerController.Services;
using VdaHubServerController.UI;

namespace VdaHubServerController
{
public static class InstallFlowProgram
{
    public static int Run(string[] args) => RunPcAgentUi(args);
'@ + [Environment]::NewLine + $methods + [Environment]::NewLine + @'
}

public static class FlowFixture
{
    public static readonly List<string> Events = new();
    public static bool Enrolled;
    public static bool InstallOk;
    public static bool StartOk;
    public static string ServiceStatus = "";
    public static DialogResult StatusResult;
    public static DialogResult EnrollmentResult;
    public static void Reset()
    {
        Events.Clear();
        Enrolled = InstallOk = StartOk = true;
        ServiceStatus = "Çalışıyor (RUNNING)";
        StatusResult = DialogResult.OK;
        EnrollmentResult = DialogResult.OK;
    }
}
public enum DialogResult { OK, Retry, Cancel }
public enum MessageBoxButtons { OK }
public enum MessageBoxIcon { Information, Error }
public static class MessageBox
{
    public static void Show(string text, string title, MessageBoxButtons buttons, MessageBoxIcon icon)
        => FlowFixture.Events.Add("message:" + title);
}
public static class ApplicationConfiguration { public static void Initialize() { } }
public static class Application { public static string ExecutablePath => "fake-agent.exe"; }

public static class InstallFlowChecks
{
    public static void RunAll()
    {
        Check("existing pairing updates before status", () => { }, 0,
            "install:False", "start", "status");
        Check("closing status still applies update", () => FlowFixture.StatusResult = DialogResult.Cancel, 0,
            "install:False", "start", "status");
        Check("failed update does not claim ready", () => FlowFixture.InstallOk = false, 1,
            "install:False", "message:PC Agent servisi kurulamadı");
        Check("failed start does not show status", () => {
            FlowFixture.StartOk = false;
            FlowFixture.ServiceStatus = "Durduruldu (STOPPED)";
        }, 1, "install:False", "start", "message:PC Agent başlatılamadı");
        Check("already running service is accepted", () => FlowFixture.StartOk = false, 0,
            "install:False", "start", "status");
        Check("new pairing installs and starts", () => FlowFixture.Enrolled = false, 0,
            "enroll", "install:True", "start", "message:VDAKor PC Agent hazır");
        Check("cancelled pairing never installs", () => {
            FlowFixture.Enrolled = false;
            FlowFixture.EnrollmentResult = DialogResult.Cancel;
        }, 1, "enroll");
        Check("explicit re-enrollment is retained", () => FlowFixture.StatusResult = DialogResult.Retry, 0,
            "install:False", "start", "status", "enroll", "install:True", "start", "message:VDAKor PC Agent hazır");
        Console.WriteLine("PASS: all 8 PC Agent installation flow checks.");
    }
    private static void Check(string name, Action arrange, int expectedExit, params string[] expectedEvents)
    {
        FlowFixture.Reset();
        arrange();
        var actualExit = InstallFlowProgram.Run(Array.Empty<string>());
        if (actualExit != expectedExit || !FlowFixture.Events.SequenceEqual(expectedEvents))
            throw new Exception(name + ": exit=" + actualExit + "; events=" + string.Join(", ", FlowFixture.Events));
    }
}
}
namespace VdaHubServerController.Services
{
    public sealed class FakePcConfig { public string ComputerName => "Test PC"; }
    public sealed class PcConfigService
    {
        public FakePcConfig Current { get; } = new();
        public void Reset() { FlowFixture.Enrolled = false; FlowFixture.Events.Add("reset-config"); }
    }
    public sealed class PcEnrollmentService
    {
        public PcEnrollmentService(PcConfigService config) { }
        public static bool IsEnrolled(FakePcConfig config) => FlowFixture.Enrolled;
    }
    public static class PcWindowsServiceManager
    {
        public static (bool Success, string Output) InstallService(string path, bool forceRestart)
        {
            FlowFixture.Events.Add("install:" + forceRestart);
            return (FlowFixture.InstallOk, "test install");
        }
        public static (bool Success, string Output) StartService()
        {
            FlowFixture.Events.Add("start");
            return (FlowFixture.StartOk, "test start");
        }
        public static string GetServiceStatus() => FlowFixture.ServiceStatus;
        public static void ResetService() => FlowFixture.Events.Add("reset-service");
    }
}
namespace VdaHubServerController.UI
{
    public sealed class PcStatusForm : IDisposable
    {
        public PcStatusForm(PcConfigService config) { }
        public DialogResult ShowDialog() { FlowFixture.Events.Add("status"); return FlowFixture.StatusResult; }
        public void Dispose() { }
    }
    public sealed class PcEnrollmentForm : IDisposable
    {
        public PcEnrollmentForm(PcEnrollmentService enrollment) { }
        public DialogResult ShowDialog() { FlowFixture.Events.Add("enroll"); return FlowFixture.EnrollmentResult; }
        public void Dispose() { }
    }
}
'@

Add-Type -TypeDefinition $source
[VdaHubServerController.InstallFlowChecks]::RunAll()
