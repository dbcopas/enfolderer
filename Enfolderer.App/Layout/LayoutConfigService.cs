using System;

namespace Enfolderer.App.Layout;

public class LayoutConfigService
{
    public (int rows,int cols,string canonicalToken) ApplyToken(string token)
    {
        switch (token.ToLowerInvariant())
        {
            case "3x3": return (3,3,"3x3");
            case "2x2": return (2,2,"2x2");
            default: return (3,4,"4x3");
        }
    }
}
