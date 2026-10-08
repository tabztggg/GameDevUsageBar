# Screenshot provenance

These images are rendered from actual GameDevUsageBar WPF views using isolated synthetic test fixtures. They contain no production credentials or account identifiers and are not evidence of live vendor access.

| File | Source fixture and render |
| --- | --- |
| `overview-en-US.png`, `overview-zh-CN.png` | `OverviewDesignChecks.cs`: `overview-*-1280-150.png` |
| `floating-bar-en-US.png`, `floating-bar-zh-CN.png` | `StripDesignChecks.cs`: `strip-*-150.png` |
| `hover-en-US.png`, `hover-zh-CN.png` | `ProviderHoverChecks.cs`: `normal-two-codex-hover-*-150.png` |
| `provider-popup-en-US.png`, `provider-popup-zh-CN.png` | `ProviderHoverChecks.cs`: `normal-two-codex-click-*-150.png` |
| `accounts-en-US.png` | `MultiAccountUiChecks.cs`: `multi-account-native-manager-150.png` |

All fixture sources are under `tests/GameDevUsageBar.AppTests/`. The v0.9.8 hover and click images use two synthetic Codex accounts in a 500-DIP-wide panel. They show the same metrics through the shared provider-account view; the clicked panel adds per-account actions. The **Displayed account** badge means display selection only. An enabled or disabled rendered button is not proof of a successful native auth switch.

The renderer uses a 150% output scale. This is not a claim that every physical monitor, DPI transition, or screen-reader setup has been tested. Names, balances, plans, and dates are fixture values; they do not establish account or plan entitlements.
