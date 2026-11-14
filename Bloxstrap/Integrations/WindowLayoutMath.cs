namespace Bloxstrap.Integrations
{
    internal static class WindowLayoutMath
    {
        public static (int X,int Y,int Width,int Height) Fit(double x,double y,double width,double height,int left,int top,int areaWidth,int areaHeight)
        {
            if(areaWidth<=0 || areaHeight<=0) throw new ArgumentOutOfRangeException(nameof(areaWidth));
            double Fraction(double v,double fallback) => double.IsFinite(v) ? Math.Clamp(v,0,1) : fallback;
            int w=Math.Clamp((int)(Fraction(width,.5)*areaWidth),Math.Min(320,areaWidth),areaWidth);
            int h=Math.Clamp((int)(Fraction(height,.5)*areaHeight),Math.Min(240,areaHeight),areaHeight);
            return (left+Math.Min((int)(Fraction(x,0)*areaWidth),areaWidth-w),
                top+Math.Min((int)(Fraction(y,0)*areaHeight),areaHeight-h),w,h);
        }
    }
}
