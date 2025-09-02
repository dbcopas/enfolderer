namespace Enfolderer.App;

/// <summary>
/// Thin wrapper to rebuild navigation views given current face count and layout parameters.
/// </summary>
public class NavigationViewBuilder
{
    private readonly NavigationService _nav;
    public NavigationViewBuilder(NavigationService nav) { _nav = nav; }
    public void Rebuild(int faceCount, int slotsPerPage, int pagesPerBinder) => _nav.Rebuild(faceCount, slotsPerPage, pagesPerBinder);
}
