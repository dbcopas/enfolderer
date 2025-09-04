using System.Threading.Tasks;
using Enfolderer.App.Binder;

namespace Enfolderer.App.Core.Abstractions;

public interface IBinderLoadService
{
    Task<BinderLoadResult> LoadAsync(string path, int slotsPerPage);
}
