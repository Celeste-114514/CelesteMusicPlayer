using System.Text.Json;
using System.Threading.Tasks;
namespace CelesteMusicPlayer;
// Compatibility adapter only. Production web UI calls WebAppearanceService directly.
public sealed partial class MainWindow
{
    private readonly WebAppearanceService _appearanceService = new();
    internal Task<object> WebAppearanceAsync(JsonElement message) => _appearanceService.ExecuteAsync(message, PickerHostHandle());
}
