using Planet.Generation;

namespace Planet.Service;

/// <summary>
/// 目前用來生成星球的地形參數（Singleton）。可以透過 <see cref="Controllers.RegenerationController"/> 讀取／更新，
/// 更新本身不會立刻改變已經生成好的星球——要呼叫「重新生成」才會套用新參數重新產生整顆星球。
/// </summary>
public class TerrainOptionsStore
{
    private TerrainGenerationOptions _current = TerrainGenerationOptions.Default;

    /// <summary>目前的地形參數。星球啟動時如果需要生成，也是用這一份（預設值＝ <see cref="TerrainGenerationOptions.Default"/>）。</summary>
    public TerrainGenerationOptions Current => _current;

    /// <summary>更新目前的地形參數，供下一次「重新生成」使用。</summary>
    public void Set(TerrainGenerationOptions options) => _current = options;
}
