namespace DragonQuotaWidget;

public static class WidgetPlacement
{
    public static double GetFacingScaleX(double dragonCenterX, double workAreaLeft, double workAreaWidth)
    {
        if (double.IsNaN(dragonCenterX) || double.IsInfinity(dragonCenterX) || workAreaWidth <= 0) return 1d;

        // The original artwork faces left. Mirror it on the left half so the
        // character always looks inward toward the screen center.
        return dragonCenterX < workAreaLeft + workAreaWidth / 2d ? -1d : 1d;
    }
}
