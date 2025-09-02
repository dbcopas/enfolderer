// Moved into AppRoot for organization
using System;
using Enfolderer.App.Core;
using Enfolderer.App.Layout;
using System.Windows.Input;

namespace Enfolderer.App;

public class CommandFactory
{
    private readonly NavigationService _nav;
    private readonly Func<int> _pagesPerBinder;
    private readonly Func<int> _orderedFaceCount;
    private readonly Func<string> _jumpBinderInput;
    private readonly Func<string> _jumpPageInput;
    private readonly Func<int> _slotsPerPage;
    private readonly Func<System.Collections.Generic.IReadOnlyList<CardEntry>> _orderedFacesAccessor;

    public CommandFactory(NavigationService nav,
                          Func<int> pagesPerBinder,
                          Func<int> orderedFaceCount,
                          Func<string> jumpBinderInput,
                          Func<string> jumpPageInput,
                          Func<int> slotsPerPage,
                          Func<System.Collections.Generic.IReadOnlyList<CardEntry>> orderedFacesAccessor)
    { _nav = nav; _pagesPerBinder = pagesPerBinder; _orderedFaceCount = orderedFaceCount; _jumpBinderInput = jumpBinderInput; _jumpPageInput = jumpPageInput; _slotsPerPage = slotsPerPage; _orderedFacesAccessor = orderedFacesAccessor; }

    private bool TryParseJump(out int binder, out int page)
    { binder = page = 0; if (!int.TryParse(_jumpBinderInput(), out binder) || binder < 1) return false; var ppb = _pagesPerBinder(); if (!int.TryParse(_jumpPageInput(), out page) || page < 1 || page > ppb) return false; return true; }

    public ICommand CreateNext() => new RelayCommand(_ => { if (_nav.CanNext) _nav.Next(); }, _ => _nav.CanNext);
    public ICommand CreatePrev() => new RelayCommand(_ => { if (_nav.CanPrev) _nav.Prev(); }, _ => _nav.CanPrev);
    public ICommand CreateFirst() => new RelayCommand(_ => { if (_nav.CanFirst) _nav.First(); }, _ => _nav.CanFirst);
    public ICommand CreateLast()  => new RelayCommand(_ => { if (_nav.CanLast)  _nav.Last();  }, _ => _nav.CanLast);
    public ICommand CreateNextBinder() => new RelayCommand(_ => { if (_nav.CanJumpBinder(1)) _nav.JumpBinder(1); }, _ => _nav.CanJumpBinder(1));
    public ICommand CreatePrevBinder() => new RelayCommand(_ => { if (_nav.CanJumpBinder(-1)) _nav.JumpBinder(-1); }, _ => _nav.CanJumpBinder(-1));
    public ICommand CreateJumpToPage() => new RelayCommand(_ => { if (TryParseJump(out int b, out int p) && _nav.CanJumpToPage(b,p,_pagesPerBinder())) _nav.JumpToPage(b,p,_pagesPerBinder()); }, _ => TryParseJump(out int b, out int p) && _nav.CanJumpToPage(b,p,_pagesPerBinder()));
    public ICommand CreateNextSet() => new RelayCommand(_ => { var faces = _orderedFacesAccessor(); if (_nav.CanJumpSet(true, faces.Count)) _nav.JumpSet(true, faces, _slotsPerPage(), c => c.Set); }, _ => _nav.CanJumpSet(true, _orderedFaceCount()));
    public ICommand CreatePrevSet() => new RelayCommand(_ => { var faces = _orderedFacesAccessor(); if (_nav.CanJumpSet(false, faces.Count)) _nav.JumpSet(false, faces, _slotsPerPage(), c => c.Set); }, _ => _nav.CanJumpSet(false, _orderedFaceCount()));
}
