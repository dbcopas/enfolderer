namespace Enfolderer.App.Tests;

// Lightweight self-check harness (no external test framework yet)
public static class LayoutConfigServiceTests
{
    public static int RunAll()
    {
        int failures = 0;
        void Check(string token,int er,int ec,string ecannon)
        {
            var svc = new LayoutConfigService();
            var (r,c,canon) = svc.ApplyToken(token);
            if (r!=er || c!=ec || canon!=ecannon) failures++;
        }
        Check("4x3",3,4,"4x3");
        Check("3x3",3,3,"3x3");
        Check("2x2",2,2,"2x2");
        Check("unknown",3,4,"4x3");
        Check("4X3",3,4,"4x3");
        var svc2 = new LayoutConfigService();
        var first = svc2.ApplyToken("3x3");
        var second = svc2.ApplyToken(first.canonicalToken);
        if (first!=second) failures++;
        return failures;
    }
}
