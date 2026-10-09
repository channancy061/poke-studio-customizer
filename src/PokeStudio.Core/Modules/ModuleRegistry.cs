namespace PokeStudio.Core.Modules;

/// <summary>
/// Holds the game modules compiled into the app. Modules are registered at startup rather than
/// loaded from arbitrary DLLs: single-file publishing doesn't support dynamic loading well, and
/// running unknown code from disk is a security risk.
/// </summary>
public sealed class ModuleRegistry
{
    private readonly List<IGameModule> _modules = [];

    public IReadOnlyList<IGameModule> Modules => _modules;

    public ModuleRegistry Register(IGameModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        if (_modules.Any(m => m.Id == module.Id))
        {
            throw new InvalidOperationException($"A module with id '{module.Id}' is already registered.");
        }

        _modules.Add(module);
        return this;
    }

    public IGameModule? Find(string id) => _modules.FirstOrDefault(m => m.Id == id);

    public IGameModule Get(string id) =>
        Find(id) ?? throw new KeyNotFoundException($"No module with id '{id}' is installed.");

    /// <summary>Asks every module whether it recognises the source; returns the best match or null.</summary>
    public (IGameModule Module, DetectionResult Result)? Detect(GameSource source)
    {
        (IGameModule Module, DetectionResult Result)? best = null;
        foreach (var module in _modules)
        {
            DetectionResult result;
            try
            {
                result = module.Detect(source);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                continue; // a module failing to read a file must not block the others
            }

            if (result.IsMatch && (best is null || result.Confidence > best.Value.Result.Confidence))
            {
                best = (module, result);
            }
        }

        return best;
    }
}
