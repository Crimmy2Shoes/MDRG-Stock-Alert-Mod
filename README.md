# Stock Market Overhaul Mod by PunPun (MDRG 0.95.X)

A comprehensive stock market overhaul mod for **My Dystopian Robot Girlfriend** (MelonLoader).

Your bot monitors your portfolio in real-time and alerts you through in-game dialogue when stocks hit key thresholds - complete with facial expressions, mood-aware dialogue lines, and a fully redesigned stock website UI.

---

## Features

- **Real-time stock alerts** - Gain and loss threshold notifications delivered as in-game dialogue
- **All-Time High / Low tracking** - Session-based ATH/ATL alerts with a dedicated display panel
- **Stock forecast** - Predicts stock direction (Strong Buy / Buy / Hold / Sell / Strong Sell) using internal game data
- **Custom portfolio display** - Redesigned stock website layout with per-stock P&L, average price, and total portfolio summary
- **Company banners** - Custom banner art for each company displayed on the stock website
- **Facial expressions** - Companion reacts with mood-appropriate expressions during stock alerts
- **Sympathy-aware dialogue** - Unique dialogue lines that change based on sympathy level.
- **In-game item unlock** - All features activate only after the player buys and uses the **Stock Alert Module** item from the parts shop
- **Fully configurable** - All thresholds, toggles, and features adjustable via Mod Settings Menu (MSM)

---

## Requirements

- [MelonLoader](https://github.com/lavagang/melonloader) (v0.7.2)
- [ModSettingsMenu (MSM) by MrEcho ](https://github.com/Echo5Dev/MDRG-ModSettingsMenu) (v1.1.0) - for in-game configuration panel
- Mods.zip and StockAlertAssets.zip from release.
---

## Platform Support

- **Windows** - Fully supported (tested on Windows 10/11)

---


## Installation

### Step 1: Install the MelonLoader mod

Extract the contents of Mods.zip into your game directory:

```
<Game Directory>/Mods/StockAlertMod.dll
<Game Directory>/Mods/StockAlertAssets
```

### Step 2: Install the in-game item mod

Install the `StockAlertModule.zip` using MDRG's mod manager.

This adds the **Stock Alert Module** item to the **ladyparts.ic** shop for $150,000.
**THE MOD WILL NOT WORK IF YOU DONT DO THIS!** 
### Final folder structure

```
<Game Directory>/
    Mods/
        StockAlertMod.dll
        StockAlertAssets/
            StockAlertLines.json
            Banners/
                FeetCoin.png
                Incontinent Cell.png
                Cock Twitch.png
                bang.ic.png
```
---

## How It Works

1. **Buy** the Stock Alert Module from the **ladyparts.ic** parts shop
2. **Use** the Stock Alert Module item from your **ITEMS INVENTORY**, your bot will confirm installation with a short dialogue.
3. The stock website UI is now upgraded with portfolio stats, forecasts, session highs/lows, and company banners
4. Your companion will alert you during gameplay when your stocks hit configured thresholds

Without the module, the stock website remains completely unmodified.

---

## Configuration

Open the **Mod Settings Menu** in-game to configure:

| Setting | Default | Description |
|---------|---------|-------------|
| +10% | On | Alert at +10% gain |
| +25% | On | Alert at +25% gain |
| +50% | On | Alert at +50% gain |
| +75% | Off | Alert at +75% gain |
| +100% | On | Alert at +100% gain (doubled) |
| All-Time High Alert | On | Alert on new session high |
| All-Time Low Alert | On | Alert on new session low |
| Loss Alerts Enabled | On | Master toggle for loss alerts |
| -10% | On | Warn at -10% loss |
| -20% | On | Warn at -20% loss |
| -50% | Off | Warn at -50% loss |
| Mod Enabled | On | Master on/off switch |
| Alert Cooldown | 45s | Seconds between alerts (15-120) |
| Alert Queue Cap | 3 | Max alerts queued at once (1-5) |
| Plain Alerts | Off | Skip dialogue flavour - show raw stats only |
| Facial Expressions | On | Enable companion expressions during alerts |
| Verbose Logging | Off | Debug logging for troubleshooting |

---

## FAQ/Troubleshooting
- **No alerts / vanilla website?** - You need to buy and use the Stock Alert Module item ONCE, from the ladyparts.ic shop first.
- **Banners not showing?** - Check that filenames match in-game company names. Check the MelonLoader console for load messages.
- **Expressions not working?** - Ensure "Facial Expressions" is enabled in MSM.
- **UI layout broken?** - Close and reopen the stock website.
- **Any Plans for Android?** - Unless lemonloader gets updated, no.
---

## Credits

Made by **PunPun**
Special Thanks to **MrEcho** for allowing me to utilize MSM for my mod.
Special Thanks to **Ivory61** for recommending tools used in development.
Special Thanks to **Sheep** for giving information about code.
