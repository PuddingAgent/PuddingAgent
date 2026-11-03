using System.Runtime.InteropServices;
using Pudding.Contracts.Computer;
using PuddingComputer.Windows;

namespace PuddingComputer.WindowsTests;

[TestClass]
public sealed class DesktopInputTests
{
    internal static DesktopWindowState State => new(100, 20, 123, 3, new(-100, 0, 800, 600),
        new(-100, 30, 800, 570), 96, true, false, 100, 100, 100, 100, 0, 0, 0);

    [TestMethod]
    public void InputLayoutIncludesMouseUnionOnBothArchitectures()
    {
        Assert.AreEqual(IntPtr.Size == 8 ? 40 : 28, Marshal.SizeOf<NativeDesktopInput>());
        Assert.AreEqual(IntPtr.Size == 8 ? 8 : 4, Marshal.OffsetOf<NativeDesktopInput>(nameof(NativeDesktopInput.Data)).ToInt32());
    }

    [TestMethod]
    public void ChromiumRootFocusWithoutNativeCaretIsAllowedButNeverVerified()
    {
        var fake = new Fake();
        var result = new WindowsDesktopInput(fake).TypeText(100, "a中");
        Assert.AreEqual(ComputerCompletion.Dispatched, result.Receipt.Completion);
        Assert.AreEqual(ComputerVerification.Unverified, result.Receipt.Verification);
        Assert.AreEqual(4, result.Receipt.SubmittedInputCount);
        Assert.IsFalse(result.Receipt.SafeToRetry);
    }

    [TestMethod]
    public void KeyboardRefusesFocusInAnotherRoot()
    {
        var fake = new Fake { Window = State with { Focus = 200, FocusRoot = 200 } };
        var result = new WindowsDesktopInput(fake).Key(100, "Ctrl+V");
        Assert.AreEqual(ComputerCompletion.Blocked, result.Receipt.Completion);
        Assert.HasCount(0, fake.Sends);
    }

    [TestMethod]
    public void ClickRefusesAnotherWindowEvenWhenPointerCanMoveThere()
    {
        var fake = new Fake { Hit = 200 };
        var result = new WindowsDesktopInput(fake).Click(100, new(10, 100), steps: 1);
        Assert.AreEqual("unexpected_window_at_point", result.Receipt.ErrorCode);
        Assert.HasCount(0, fake.Sends);
    }

    [TestMethod]
    public void ClickChecksLandingAndDoesNotClickClampedCoordinates()
    {
        var fake = new Fake { ClampCursor = true };
        var result = new WindowsDesktopInput(fake).Click(100, new(10, 100), steps: 1);
        Assert.AreEqual("cursor_did_not_land", result.Receipt.ErrorCode);
        Assert.HasCount(0, fake.Sends);
    }

    [TestMethod]
    public void ClickRechecksOwnershipAfterMovement()
    {
        var fake = new Fake { OccludeOnMove = true };
        var result = new WindowsDesktopInput(fake).Click(100, new(10, 100), steps: 1);
        Assert.AreEqual("unexpected_window_at_point", result.Receipt.ErrorCode);
        Assert.HasCount(0, fake.Sends);
    }

    [TestMethod]
    public void PartialChordKeepsCountReleasesKeysAndNeverReplaysChord()
    {
        var fake = new Fake { AcceptCount = 1 };
        var result = new WindowsDesktopInput(fake).Key(100, "Ctrl+V");
        Assert.AreEqual(ComputerCompletion.Failed, result.Receipt.Completion);
        Assert.AreEqual(1, result.Receipt.SubmittedInputCount);
        Assert.AreEqual(4, result.Receipt.ExpectedInputCount);
        Assert.IsFalse(result.Receipt.SafeToRetry);
        Assert.HasCount(2, fake.Sends);
        Assert.HasCount(1, fake.Sends[1]);
        Assert.AreEqual((ushort)0x11, fake.Sends[1][0].Data.Key.VirtualKey);
        Assert.IsFalse(fake.Sends[1][0].IsDown);
    }

    [TestMethod]
    public void ExistingPhysicalModifierBlocksShortcutWithoutChangingKeyState()
    {
        var fake = new Fake { Modifiers = true };
        Assert.AreEqual("modifier_already_pressed", new WindowsDesktopInput(fake).Key(100, "Ctrl+V").Receipt.ErrorCode);
        Assert.HasCount(0, fake.Sends);
    }

    [TestMethod]
    public void ForegroundLossAfterDispatchIsNotReportedAsSafeToRetry()
    {
        var fake = new Fake { LoseFocusOnSend = true };
        var result = new WindowsDesktopInput(fake).Key(100, "A");
        Assert.AreEqual("foreground_changed", result.Receipt.ErrorCode);
        Assert.IsTrue(result.Receipt.MayHaveSideEffects);
        Assert.IsFalse(result.Receipt.SafeToRetry);
    }

    [TestMethod]
    public void LostWindowAfterDispatchPreservesAcceptedCountAndUnknownOutcome()
    {
        var fake = new Fake { ThrowInspectAfterSend = true };
        var result = new WindowsDesktopInput(fake).Key(100, "A");
        Assert.AreEqual(ComputerCompletion.Unknown, result.Receipt.Completion);
        Assert.AreEqual(2, result.Receipt.SubmittedInputCount);
        Assert.IsFalse(result.Receipt.SafeToRetry);
    }

    [TestMethod]
    public void ChordValidationHappensBeforeDispatch()
    {
        var fake = new Fake();
        Assert.ThrowsExactly<ArgumentException>(() => new WindowsDesktopInput(fake).Key(100, "Ctrl+Ctrl+V"));
        Assert.HasCount(0, fake.Sends);
    }

    [TestMethod]
    public void OldObservationCannotSurviveGeometryIdentityOrTimeChanges()
    {
        var now = DateTimeOffset.UtcNow;
        var observation = new DesktopObservation("obs", State, new(State.ClientBounds, 800, 570, ComputerCaptureMode.ScreenPixels, true), [], 0, now, now.AddSeconds(45), [], []);
        Assert.IsNull(ComputerDesktopSession.ValidateObservation(observation, State, now));
        Assert.AreEqual("window_geometry_changed", ComputerDesktopSession.ValidateObservation(observation, State with { Dpi = 144 }, now));
        Assert.AreEqual("window_geometry_changed", ComputerDesktopSession.ValidateObservation(observation, State with { ClientBounds = new(20, 20, 800, 570) }, now));
        Assert.AreEqual("window_identity_changed", ComputerDesktopSession.ValidateObservation(observation, State with { ProcessStartTimeUtcTicks = 456 }, now));
        Assert.AreEqual("observation_expired", ComputerDesktopSession.ValidateObservation(observation, State, now.AddSeconds(45)));
    }

    private sealed class Fake : IWindowsInputPlatform
    {
        public DesktopWindowState Window = State;
        public long Hit = 100;
        public bool ClampCursor, OccludeOnMove, Modifiers, LoseFocusOnSend, ThrowInspectAfterSend;
        public int? AcceptCount;
        public ScreenPoint Position;
        public List<NativeDesktopInput[]> Sends { get; } = [];
        public DesktopWindowState Inspect(long handle) => ThrowInspectAfterSend && Sends.Count > 0
            ? throw new InvalidOperationException("Window closed") : Window;
        public void Activate(long handle, bool repairNativeFocus) { }
        public ScreenPoint Cursor() => Position;
        public bool Move(ScreenPoint point) { Position = ClampCursor ? new(0, 0) : point; if (OccludeOnMove) Hit = 200; return true; }
        public long HitRoot(ScreenPoint point) => Hit;
        public bool OnMonitor(ScreenPoint point) => true;
        public bool HasPressedModifiers() => Modifiers;
        public (int Submitted, int? Error) Send(NativeDesktopInput[] inputs)
        {
            Sends.Add(inputs);
            if (LoseFocusOnSend) Window = Window with { ForegroundRoot = 200 };
            return (AcceptCount ?? inputs.Length, AcceptCount.HasValue ? 5 : null);
        }
        public IDisposable EnterCoordinates() => new EmptyScope();
        private sealed class EmptyScope : IDisposable { public void Dispose() { } }
    }
}
