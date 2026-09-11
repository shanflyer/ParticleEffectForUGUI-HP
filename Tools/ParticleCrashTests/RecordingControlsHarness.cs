// Production controller methods, with managed UI/recorder stand-ins. No Unity launch.
using System;
namespace FxUIParticleTest
{
    class Text { public string text; }
    class Button { public bool interactable; }
    class FxProfilerRecorder
    {
        public bool IsRecording, IsSampling;
        public int starts, stops;
        public string lastCase;
        public void BeginRecording(string name) { starts++; IsRecording = true; IsSampling = false; lastCase = name; }
        public void StopAndWrite() { if (IsRecording) stops++; IsRecording = IsSampling = false; }
    }
    partial class FxBaselineController
    {
        public bool autoRecord, isActiveAndEnabled = true;
        bool _memoryLimitReached;
        FxProfilerRecorder _recorder = new FxProfilerRecorder();
        Button _startRecordBtn = new Button(), _stopRecordBtn = new Button();
        Text _startRecordText = new Text();
        int _recordControlState = -1;
        string currentCase = "G";
        string RecordingName() => currentCase;
        static int passed, failed;
        static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
        static void Test(string name, Action action)
        {
            try { action(); passed++; Console.WriteLine("PASS " + name); }
            catch(Exception e) { failed++; Console.WriteLine("FAIL " + name + ": " + e.Message); }
        }
        static int Main()
        {
            Test("manual idle switches never start recording", () => {
                var c = new FxBaselineController(); c.RestartRecordingForStateChange(); c.UpdateRecordingControls();
                Assert(c._recorder.starts == 0 && c._startRecordBtn.interactable && !c._stopRecordBtn.interactable, "idle capture started");
            });
            Test("start transitions through warmup and sampling without duplicate starts", () => {
                var c = new FxBaselineController(); c.StartRecording(); c.StartRecording();
                Assert(c._recorder.starts == 1 && c._startRecordText.text == "Warmup..." && !c._startRecordBtn.interactable && c._stopRecordBtn.interactable, "start state incorrect");
                c._recorder.IsSampling = true; c.UpdateRecordingControls();
                Assert(c._startRecordText.text == "Recording", "sampling status missing");
            });
            Test("manual stop persists through later optimization changes", () => {
                var c = new FxBaselineController { autoRecord = true }; c.StartRecording(); c.StopRecording();
                c.StopRecording(); c.RestartRecordingForStateChange();
                Assert(!c.autoRecord && c._recorder.starts == 1 && c._recorder.stops == 1 && !c._recorder.IsRecording, "manual stop overridden");
                Assert(c._startRecordBtn.interactable && !c._stopRecordBtn.interactable, "stop controls incorrect");
            });
            Test("active manual recording splits when configuration changes", () => {
                var c = new FxBaselineController(); c.StartRecording(); c.currentCase = "G MergeON";
                c.RestartRecordingForStateChange();
                Assert(!c.autoRecord && c._recorder.starts == 2 && c._recorder.lastCase == c.currentCase, "mixed configurations");
            });
            Test("duration completion restores start control", () => {
                var c = new FxBaselineController(); c.StartRecording(); c._recorder.StopAndWrite(); c.UpdateRecordingControls();
                Assert(c._startRecordBtn.interactable && !c._stopRecordBtn.interactable && c._startRecordText.text == "Start Rec", "duration completion left stale controls");
            });
            Test("memory stop cannot restart recording", () => {
                var c = new FxBaselineController { _memoryLimitReached = true }; c.StartRecording(); c.UpdateRecordingControls();
                Assert(c._recorder.starts == 0 && !c._startRecordBtn.interactable, "safety stop bypassed");
            });
            Console.WriteLine($"RESULT {passed} passed, {failed} failed"); return failed == 0 ? 0 : 1;
        }
    }
}
