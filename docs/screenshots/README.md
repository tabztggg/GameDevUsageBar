# Screenshot provenance

These images are rendered from actual GameDevUsageBar WPF views using isolated synthetic test fixtures. They contain no production credentials or account identifiers and are not evidence of live vendor access.

| File | Source fixture and render |
| --- | --- |
| `overview-en-US.png`, `overview-zh-CN.png` | `OverviewDesignChecks.cs`: `overview-*-1280-150.png` |
| `floating-bar-en-US.png`, `floating-bar-zh-CN.png` | `StripDesignChecks.cs`: `strip-*-150.png` |
| `hover-en-US.png`, `hover-zh-CN.png` | `CompactDesignChecks.cs`: `tooltip-*-150.png` |
| `provider-popup-en-US.png`, `provider-popup-zh-CN.png` | `FooterAccountSwitchChecks.cs`: `footer-popup-*-440-150.png` |
| `accounts-en-US.png` | `MultiAccountUiChecks.cs`: `multi-account-native-manager-150.png` |

All fixture sources are under `tests/GameDevUsageBar.AppTests/`. The provider popup uses the 440-DIP render to keep its footer and dates readable. Its **Switch account** action is shown in the actual WPF layout; rendering an enabled button alone is not evidence of a successful native auth switch.

The renderer uses a 150% output scale. This is not a claim that every physical monitor, DPI transition, or screen-reader setup has been tested. Labels, balances, plans, and dates are sample values.
