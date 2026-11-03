using OpenCvSharp;
using Pudding.Contracts.Computer;
using PuddingComputer.Automation;

namespace PuddingComputer.Windows;

/// <summary>
/// Proposes regions from the captured pixels. Results are candidates, never proof
/// of role, focus or clickability. Implementations must retain source attribution;
/// OCR/model adapters can supply labels and calibrated confidence when available.
/// Rectangles are physical screen pixels relative to the supplied capture bounds.
/// </summary>
public interface IComputerElementDetector
{
    IReadOnlyList<RawDetection> Detect(Mat bgr, PhysicalRect screenBounds, PhysicalRect? focusRect = null);
}
