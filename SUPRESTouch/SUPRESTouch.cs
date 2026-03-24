#region Using declarations

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.DrawingTools;

#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
public class SUPRESTouch : Indicator
{
private const double TOL = 0.25;
private static readonly HttpClient Http = new HttpClient();
private const int SERIES_15MIN = 1;

private string stateFilePath;
private Dictionary<string, string> levelBias;
private Dictionary<string, bool> levelFired;
private Dictionary<string, bool> levelBuyLocked;
private Dictionary<string, bool> levelSellLocked;
private Dictionary<string, int> levelLastTradeBar;
private Dictionary<string, bool> levelNeedsReset;

private DateTime lastCloseTime;
private bool positionWasOpen;
private bool inPosition;
private double lastEntryPrice;
private string lastEntryAction;
private string lastEntryTag;

private int tradesToday;
private int stopLossesToday;
private bool dailyLimitHit;
private DateTime lastTradeDate;
private int consecWins;
private bool consecWinLimitHit;
private SMA sma20series;

protected override void OnStateChange()
{
if (State == State.SetDefaults)
{
Description = "SUPRES Touch Detection - CrossTrade auto-order";
Name = "SUPRESTouch";
Calculate = Calculate.OnEachTick;
IsOverlay = true;
DisplayInDataBox = false;
DrawOnPricePanel = true;
IsSuspendedWhileInactive = true;

S_Key = "YOUR-SECRET-KEY";
S_Webhook = "https://hooks.crosstrade.io/v1/YOUR-WEBHOOK-ID";
S_Account = "sim101";
S_Instrument = "ES JUN26";
S_Qty = 1;
S_Flatten = true;
S_TP = 20;
S_SL = 10;
S_Cooldown = 0;
S_MaxTrades = 7;
S_MaxSLHits = 3;
S_ConsecWins = 5;

S_TelegramToken = "YOUR-BOT-TOKEN";
S_TelegramChatId = "YOUR-CHAT-ID";
S_TelegramEnabled = true;

S_News0Start = new TimeSpan(4, 30, 0);
S_News0End = new TimeSpan(6, 0, 0);
S_News1Start = new TimeSpan(6, 25, 0);
S_News1End = new TimeSpan(6, 35, 0);
S_News2Start = new TimeSpan(7, 55, 0);
S_News2End = new TimeSpan(8, 5, 0);
S_News3Start = new TimeSpan(11, 55, 0);
S_News3End = new TimeSpan(12, 5, 0);
S_NewsEnabled = true;

S_UseL1 = true; S_L1 = 5000.00;
S_UseL2 = true; S_L2 = 5025.00;
S_UseL3 = true; S_L3 = 4975.00;
S_UseL4 = false; S_L4 = 5050.00;
S_UseL5 = false; S_L5 = 4950.00;
S_UseL6 = false; S_L6 = 5075.00;
S_UseL7 = false; S_L7 = 4925.00;
S_UseL8 = false; S_L8 = 5100.00;
S_UseSMA = true;
S_Arrows = true;
}
else if (State == State.Configure)
{
AddDataSeries(Data.BarsPeriodType.Minute, 15);
}
else if (State == State.DataLoaded)
{
levelBias = new Dictionary<string, string>();
levelFired = new Dictionary<string, bool>();
levelBuyLocked = new Dictionary<string, bool>();
levelSellLocked = new Dictionary<string, bool>();
levelLastTradeBar = new Dictionary<string, int>();
levelNeedsReset = new Dictionary<string, bool>();

lastCloseTime = DateTime.MinValue;
positionWasOpen = false;
inPosition = false;
lastEntryPrice = 0;
lastEntryAction = "";
lastEntryTag = "";

stateFilePath = Path.Combine(
Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
"NinjaTrader 8", "SUPRESTouch_State.txt");

LoadState();

sma20series = SMA(20);

Print("SUPRESTouch | Loaded — trades today: " + tradesToday
+ " | SL hits: " + stopLossesToday
+ " | Win streak: " + consecWins
+ " | Date: " + lastTradeDate.ToString("MM/dd/yyyy"));
}
}

private void SaveState()
{
try
{
var lines = new List<string>
{
"Date=" + lastTradeDate.ToString("yyyy-MM-dd"),
"TradesToday=" + tradesToday.ToString(),
"SLHitsToday=" + stopLossesToday.ToString(),
"DailyLimitHit=" + dailyLimitHit.ToString(),
"ConsecWins=" + consecWins.ToString(),
"ConsecWinLimit="+ consecWinLimitHit.ToString()
};

File.WriteAllLines(stateFilePath, lines);
}
catch (Exception ex)
{
Print("SUPRESTouch | SaveState error: " + ex.Message);
}
}

private void LoadState()
{
try
{
tradesToday = 0;
stopLossesToday = 0;
dailyLimitHit = false;
consecWins = 0;
consecWinLimitHit = false;
lastTradeDate = DateTime.MinValue;

if (!File.Exists(stateFilePath)) return;

string[] lines = File.ReadAllLines(stateFilePath);

TimeZoneInfo mtZone = TimeZoneInfo.FindSystemTimeZoneById(
"Mountain Standard Time");
DateTime mtNow = TimeZoneInfo.ConvertTime(DateTime.Now, mtZone);
DateTime savedDate = DateTime.MinValue;

foreach (string line in lines)
{
string[] parts = line.Split('=');
if (parts.Length != 2) continue;

string key = parts[0].Trim();
string val = parts[1].Trim();

switch (key)
{
case "Date":
DateTime.TryParse(val, out savedDate);
break;
case "TradesToday":
int.TryParse(val, out tradesToday);
break;
case "SLHitsToday":
int.TryParse(val, out stopLossesToday);
break;
case "DailyLimitHit":
bool.TryParse(val, out dailyLimitHit);
break;
case "ConsecWins":
int.TryParse(val, out consecWins);
break;
case "ConsecWinLimit":
bool.TryParse(val, out consecWinLimitHit);
break;
}
}

if (savedDate.Date != mtNow.Date)
{
tradesToday = 0;
stopLossesToday = 0;
dailyLimitHit = false;
consecWins = 0;
consecWinLimitHit = false;
lastTradeDate = mtNow;
SaveState();
Print("SUPRESTouch | New day detected on load — counters reset");
}
else
{
lastTradeDate = savedDate;
}
}
catch (Exception ex)
{
Print("SUPRESTouch | LoadState error: " + ex.Message);
tradesToday = 0;
stopLossesToday = 0;
dailyLimitHit = false;
consecWins = 0;
consecWinLimitHit = false;
lastTradeDate = DateTime.MinValue;
}
}

protected override void OnBarUpdate()
{
if (State != State.Realtime) return;
if (CurrentBar < 20) return;
if (BarsInProgress != 0) return;
if (BarsArray[SERIES_15MIN].Count < 4) return;

CheckPositionStatus();

TimeZoneInfo mtZone = TimeZoneInfo.FindSystemTimeZoneById(
"Mountain Standard Time");
DateTime mtNow = TimeZoneInfo.ConvertTime(DateTime.Now, mtZone);
TimeSpan mtTime = mtNow.TimeOfDay;

if (mtNow.Date > lastTradeDate.Date)
{
tradesToday = 0;
stopLossesToday = 0;
dailyLimitHit = false;
consecWins = 0;
consecWinLimitHit = false;
lastTradeDate = mtNow;
SaveState();
Print("SUPRESTouch | New trading day — all counters reset");
SendTelegram("SUPRESTouch | New trading day started. All counters reset.");
}

if (dailyLimitHit)
{
Print("SUPRESTouch | Daily SL limit hit — done for today");
return;
}

if (consecWinLimitHit)
{
Print("SUPRESTouch | " + S_ConsecWins
+ " consecutive wins reached — done for today");
return;
}

if (tradesToday >= S_MaxTrades)
{
Print("SUPRESTouch | Max " + S_MaxTrades + " trades reached for today");
return;
}

if (S_NewsEnabled && IsInNewsBlackout(mtTime))
{
Print("SUPRESTouch | News blackout active — skipping");
return;
}

if (S_Cooldown > 0 && lastCloseTime > DateTime.MinValue)
{
double minSinceClose = (DateTime.Now - lastCloseTime).TotalMinutes;
if (minSinceClose < S_Cooldown)
{
Print("SUPRESTouch | Cooldown — "
+ (S_Cooldown - minSinceClose).ToString("F1")
+ " min remaining");
return;
}
}

if (inPosition) return;

int count = BarsArray[SERIES_15MIN].Count;

double c1_open = BarsArray[SERIES_15MIN].GetOpen(count - 2);
double c2_open = BarsArray[SERIES_15MIN].GetOpen(count - 3);
double c3_open = BarsArray[SERIES_15MIN].GetOpen(count - 4);

double c1_close = BarsArray[SERIES_15MIN].GetClose(count - 2);
double c2_close = BarsArray[SERIES_15MIN].GetClose(count - 3);
double c3_close = BarsArray[SERIES_15MIN].GetClose(count - 4);

double c1_high = BarsArray[SERIES_15MIN].GetHigh(count - 2);
double c2_high = BarsArray[SERIES_15MIN].GetHigh(count - 3);
double c3_high = BarsArray[SERIES_15MIN].GetHigh(count - 4);

double c1_low = BarsArray[SERIES_15MIN].GetLow(count - 2);
double c2_low = BarsArray[SERIES_15MIN].GetLow(count - 3);
double c3_low = BarsArray[SERIES_15MIN].GetLow(count - 4);

int current15Bar = count - 1;

double sma = sma20series[1];

var levels = new List<Tuple<string, double, bool>>
{
Tuple.Create("L1", S_L1, S_UseL1),
Tuple.Create("L2", S_L2, S_UseL2),
Tuple.Create("L3", S_L3, S_UseL3),
Tuple.Create("L4", S_L4, S_UseL4),
Tuple.Create("L5", S_L5, S_UseL5),
Tuple.Create("L6", S_L6, S_UseL6),
Tuple.Create("L7", S_L7, S_UseL7),
Tuple.Create("L8", S_L8, S_UseL8),
Tuple.Create("SMA20", sma, S_UseSMA),
};

foreach (var item in levels)
{
if (item.Item3)
CheckLevel(item.Item1, item.Item2,
c1_open, c2_open, c3_open,
c1_close, c2_close, c3_close,
c1_high, c2_high, c3_high,
c1_low, c2_low, c3_low,
current15Bar);
}
}

private void CheckPositionStatus()
{
try
{
Account acct = Account.All.FirstOrDefault(
a => a.Name == S_Account);

if (acct == null) return;

Position pos = acct.Positions.FirstOrDefault(
p => p.Instrument.FullName.Contains("ES"));

bool isFlat = pos == null
|| pos.MarketPosition == MarketPosition.Flat;

if (isFlat && positionWasOpen)
{
double slDistance = S_SL * TickSize;
bool wasStopLoss = false;

if (lastEntryAction == "buy"
&& Close[0] <= lastEntryPrice - slDistance)
wasStopLoss = true;

if (lastEntryAction == "sell"
&& Close[0] >= lastEntryPrice + slDistance)
wasStopLoss = true;

if (wasStopLoss)
{
consecWins = 0;
stopLossesToday++;

string slMsg = "STOP LOSS hit | " + lastEntryTag
+ " | " + lastEntryAction.ToUpper()
+ " @ " + lastEntryPrice.ToString("F2")
+ " | SL hits today: " + stopLossesToday
+ "/" + S_MaxSLHits
+ " | Win streak reset to 0";

Print("SUPRESTouch | " + slMsg);
SendTelegram("🔴 " + slMsg);

if (stopLossesToday >= S_MaxSLHits)
{
dailyLimitHit = true;
string limitMsg = "*** DAILY SL LIMIT REACHED ("
+ S_MaxSLHits + " stops) — no more trades today ***";
Print("SUPRESTouch | " + limitMsg);
SendTelegram("⛔ " + limitMsg);
}
}
else
{
consecWins++;

string tpMsg = "TAKE PROFIT hit | " + lastEntryTag
+ " | " + lastEntryAction.ToUpper()
+ " @ " + lastEntryPrice.ToString("F2")
+ " | +" + S_TP.ToString() + " ticks"
+ " | Win streak: " + consecWins + "/" + S_ConsecWins;

Print("SUPRESTouch | " + tpMsg);
SendTelegram("✅ " + tpMsg);

if (consecWins >= S_ConsecWins)
{
consecWinLimitHit = true;
string winMsg = "*** " + S_ConsecWins
+ " CONSECUTIVE WINS — great day! "
+ "Stopping trading until tomorrow ***";
Print("SUPRESTouch | " + winMsg);
SendTelegram("🏆 " + winMsg);
}
}

SaveState();

if (!string.IsNullOrEmpty(lastEntryTag))
{
levelNeedsReset[lastEntryTag] = true;
int current15Bar = BarsArray[SERIES_15MIN].Count - 1;
levelLastTradeBar[lastEntryTag] = current15Bar;

string resetMsg = lastEntryTag
+ " requires 1 fresh candle before next entry";

Print("SUPRESTouch | " + resetMsg);
SendTelegram("⏳ " + resetMsg);
}

lastCloseTime = DateTime.Now;
positionWasOpen = false;
inPosition = false;
lastEntryPrice = 0;
lastEntryAction = "";
lastEntryTag = "";

var keys = new List<string>(levelFired.Keys);
foreach (var k in keys)
levelFired[k] = false;

Print("SUPRESTouch | Position closed — watching for fresh confirmation");
}

if (!isFlat)
{
positionWasOpen = true;
inPosition = true;

if (lastEntryPrice == 0 && pos != null)
lastEntryPrice = pos.AveragePrice;
}
}
catch (Exception ex)
{
Print("SUPRESTouch | Position check error: " + ex.Message);
}
}

private bool IsInNewsBlackout(TimeSpan t)
{
return (t >= S_News0Start && t <= S_News0End)
|| (t >= S_News1Start && t <= S_News1End)
|| (t >= S_News2Start && t <= S_News2End)
|| (t >= S_News3Start && t <= S_News3End);
}

private void CheckLevel(string tag, double lvl,
double c1_open, double c2_open, double c3_open,
double c1_close, double c2_close, double c3_close,
double c1_high, double c2_high, double c3_high,
double c1_low, double c2_low, double c3_low,
int current15Bar)
{
if (!levelBuyLocked.ContainsKey(tag)) levelBuyLocked[tag] = false;
if (!levelSellLocked.ContainsKey(tag)) levelSellLocked[tag] = false;
if (!levelLastTradeBar.ContainsKey(tag)) levelLastTradeBar[tag] = -1;
if (!levelNeedsReset.ContainsKey(tag)) levelNeedsReset[tag] = false;

bool c1_bodyAbove = Math.Min(c1_open, c1_close) > lvl;
bool c2_bodyAbove = Math.Min(c2_open, c2_close) > lvl;
bool c3_bodyAbove = Math.Min(c3_open, c3_close) > lvl;

bool c1_bodyBelow = Math.Max(c1_open, c1_close) < lvl;
bool c2_bodyBelow = Math.Max(c2_open, c2_close) < lvl;
bool c3_bodyBelow = Math.Max(c3_open, c3_close) < lvl;

bool allBodiesAbove = c1_bodyAbove && c2_bodyAbove && c3_bodyAbove;
bool allBodiesBelow = c1_bodyBelow && c2_bodyBelow && c3_bodyBelow;

if (!allBodiesAbove && !allBodiesBelow) return;

string currentBias = allBodiesAbove ? "bull" : "bear";

if (levelNeedsReset[tag])
{
int lastBar = levelLastTradeBar[tag];
int c1_barIndex = BarsArray[SERIES_15MIN].Count - 2;
int c2_barIndex = BarsArray[SERIES_15MIN].Count - 3;
int c3_barIndex = BarsArray[SERIES_15MIN].Count - 4;

bool allFresh = c1_barIndex > lastBar;

if (!allFresh)
{
Print("SUPRESTouch | " + tag
+ " waiting for 1 fresh candle — need bars after "
+ lastBar + " | current c1: " + c1_barIndex);
return;
}

levelNeedsReset[tag] = false;
string freshMsg = tag + " fresh confirmation — ready to trade";
Print("SUPRESTouch | " + freshMsg);
SendTelegram("✔️ " + freshMsg);
}

bool threeConsecAbove = c1_low > lvl && c2_low > lvl && c3_low > lvl;
bool threeConsecBelow = c1_high < lvl && c2_high < lvl && c3_high < lvl;

if (levelBuyLocked[tag] && threeConsecAbove)
{
levelBuyLocked[tag] = false;
levelFired[tag] = false;
string resetMsg = tag + " BUY lock reset — 3 candles completely above";
Print("SUPRESTouch | " + resetMsg);
SendTelegram("🔄 " + resetMsg);
}

if (levelSellLocked[tag] && threeConsecBelow)
{
levelSellLocked[tag] = false;
levelFired[tag] = false;
string resetMsg = tag + " SELL lock reset — 3 candles completely below";
Print("SUPRESTouch | " + resetMsg);
SendTelegram("🔄 " + resetMsg);
}

if (levelBuyLocked[tag] && threeConsecBelow)
{
levelBuyLocked[tag] = false;
levelSellLocked[tag] = false;
levelFired[tag] = false;
Print("SUPRESTouch | " + tag
+ " direction flipped — 3 candles below while buy locked");
}

if (levelSellLocked[tag] && threeConsecAbove)
{
levelSellLocked[tag] = false;
levelBuyLocked[tag] = false;
levelFired[tag] = false;
Print("SUPRESTouch | " + tag
+ " direction flipped — 3 candles above while sell locked");
}

string savedBias;
if (levelBias.TryGetValue(tag, out savedBias))
{
if (savedBias != currentBias)
{
levelBias[tag] = currentBias;
levelFired[tag] = false;
Print("SUPRESTouch | " + tag + " bias flipped to "
+ currentBias.ToUpper() + " @ " + lvl.ToString("F2"));
}
}
else
{
levelBias[tag] = currentBias;
levelFired[tag] = false;
}

bool fired;
if (levelFired.TryGetValue(tag, out fired) && fired) return;

if (currentBias == "bull" && Low[0] <= lvl - TOL)
{
if (levelBuyLocked[tag])
{
Print("SUPRESTouch | " + tag
+ " BUY locked — waiting for 3 candles completely above");
return;
}

levelFired[tag] = true;
tradesToday++;
lastEntryAction = "buy";
lastEntryTag = tag;
SaveState();

string msg = "BUY entry | " + tag
+ " @ " + lvl.ToString("F2")
+ " | TP: " + S_TP + " ticks"
+ " | SL: " + S_SL + " ticks"
+ " | Win streak: " + consecWins
+ " | Trades today: " + tradesToday + "/" + S_MaxTrades;

Print("SUPRESTouch | " + msg);
SendTelegram("🟢 " + msg);
PostWebhook("buy", lvl);

if (S_Arrows)
Draw.ArrowUp(this, "BU_" + tag + "_" + CurrentBar.ToString(),
false, 0, Low[0] - TickSize * 6, Brushes.Teal);
}

if (currentBias == "bear" && High[0] >= lvl + TOL)
{
if (levelSellLocked[tag])
{
Print("SUPRESTouch | " + tag
+ " SELL locked — waiting for 3 candles completely below");
return;
}

levelFired[tag] = true;
tradesToday++;
lastEntryAction = "sell";
lastEntryTag = tag;
SaveState();

string msg = "SELL entry | " + tag
+ " @ " + lvl.ToString("F2")
+ " | TP: " + S_TP + " ticks"
+ " | SL: " + S_SL + " ticks"
+ " | Win streak: " + consecWins
+ " | Trades today: " + tradesToday + "/" + S_MaxTrades;

Print("SUPRESTouch | " + msg);
SendTelegram("🔴 " + msg);
PostWebhook("sell", lvl);

if (S_Arrows)
Draw.ArrowDown(this, "BD_" + tag + "_" + CurrentBar.ToString(),
false, 0, High[0] + TickSize * 6, Brushes.Red);
}
}

private void SendTelegram(string message)
{
if (!S_TelegramEnabled) return;
if (string.IsNullOrEmpty(S_TelegramToken)
|| string.IsNullOrEmpty(S_TelegramChatId)) return;

string token = S_TelegramToken;
string chatId = S_TelegramChatId;
string url = "https://api.telegram.org/bot" + token
+ "/sendMessage?chat_id=" + chatId
+ "&text=" + Uri.EscapeDataString(message);

Task.Run(async () =>
{
try { await Http.GetAsync(url); }
catch (Exception ex)
{ Print("SUPRESTouch | Telegram error: " + ex.Message); }
});
}

private void PostWebhook(string action, double lvl)
{
try
{
string flatten = S_Flatten ? "flatten_first=true;" : "";
string payload = "key=" + S_Key + ";"
+ "command=place;"
+ "account=" + S_Account + ";"
+ "instrument=" + S_Instrument + ";"
+ "action=" + action + ";"
+ "qty=" + S_Qty.ToString() + ";"
+ "order_type=market;"
+ "tif=day;"
+ flatten
+ "take_profit=" + S_TP.ToString() + " ticks;"
+ "stop_loss=" + S_SL.ToString() + " ticks;";

string url = S_Webhook;

Task.Run(async () =>
{
try
{
var resp = await Http.PostAsync(url,
new StringContent(payload, Encoding.UTF8, "text/plain"));

string body = await resp.Content.ReadAsStringAsync();

Print("SUPRESTouch | " + (resp.IsSuccessStatusCode ? "OK" : "ERR")
+ " " + action.ToUpper()
+ " @ " + lvl.ToString("F2")
+ (resp.IsSuccessStatusCode ? "" : " | " + body));
}
catch (Exception ex) { Print("SUPRESTouch | HTTP: " + ex.Message); }
});
}
catch (Exception ex) { Print("SUPRESTouch | Post: " + ex.Message); }
}

[NinjaScriptProperty]
[Display(Name="Secret Key", Order=1, GroupName="1. CrossTrade")]
public string S_Key { get; set; }

[NinjaScriptProperty]
[Display(Name="Webhook URL", Order=2, GroupName="1. CrossTrade")]
public string S_Webhook { get; set; }

[NinjaScriptProperty]
[Display(Name="NT8 Account", Order=3, GroupName="1. CrossTrade")]
public string S_Account { get; set; }

[NinjaScriptProperty]
[Display(Name="Instrument", Order=4, GroupName="1. CrossTrade")]
public string S_Instrument { get; set; }

[NinjaScriptProperty]
[Display(Name="Contracts", Order=1, GroupName="2. Order")]
public int S_Qty { get; set; }

[NinjaScriptProperty]
[Display(Name="Flatten First", Order=2, GroupName="2. Order")]
public bool S_Flatten { get; set; }

[NinjaScriptProperty]
[Display(Name="Take Profit ticks", Order=3, GroupName="2. Order")]
public int S_TP { get; set; }

[NinjaScriptProperty]
[Display(Name="Stop Loss ticks", Order=4, GroupName="2. Order")]
public int S_SL { get; set; }

[NinjaScriptProperty]
[Display(Name="Cooldown after close (minutes — set 0 to disable)", Order=5, GroupName="2. Order")]
public int S_Cooldown { get; set; }

[NinjaScriptProperty]
[Display(Name="Max trades per day", Order=6, GroupName="2. Order")]
public int S_MaxTrades { get; set; }

[NinjaScriptProperty]
[Display(Name="Max stop losses per day", Order=7, GroupName="2. Order")]
public int S_MaxSLHits { get; set; }

[NinjaScriptProperty]
[Display(Name="Consecutive wins to stop trading", Order=8, GroupName="2. Order")]
public int S_ConsecWins { get; set; }

[NinjaScriptProperty]
[Display(Name="Telegram enabled", Order=1, GroupName="3. Telegram Alerts")]
public bool S_TelegramEnabled { get; set; }

[NinjaScriptProperty]
[Display(Name="Bot Token", Order=2, GroupName="3. Telegram Alerts")]
public string S_TelegramToken { get; set; }

[NinjaScriptProperty]
[Display(Name="Chat ID", Order=3, GroupName="3. Telegram Alerts")]
public string S_TelegramChatId { get; set; }

[NinjaScriptProperty]
[Display(Name="News filter enabled", Order=1, GroupName="4. News Filter (Mountain Time)")]
public bool S_NewsEnabled { get; set; }

[NinjaScriptProperty]
[Display(Name="Block start (4:30 = connection drop window)", Order=2, GroupName="4. News Filter (Mountain Time)")]
public TimeSpan S_News0Start { get; set; }

[NinjaScriptProperty]
[Display(Name="Block end (6:00 = connection drop window)", Order=3, GroupName="4. News Filter (Mountain Time)")]
public TimeSpan S_News0End { get; set; }

[NinjaScriptProperty]
[Display(Name="News 1 start (6:25 = 8:30 EST)", Order=4, GroupName="4. News Filter (Mountain Time)")]
public TimeSpan S_News1Start { get; set; }

[NinjaScriptProperty]
[Display(Name="News 1 end (6:35 = 8:30 EST)", Order=5, GroupName="4. News Filter (Mountain Time)")]
public TimeSpan S_News1End { get; set; }

[NinjaScriptProperty]
[Display(Name="News 2 start (7:55 = 10:00 EST)", Order=6, GroupName="4. News Filter (Mountain Time)")]
public TimeSpan S_News2Start { get; set; }

[NinjaScriptProperty]
[Display(Name="News 2 end (8:05 = 10:00 EST)", Order=7, GroupName="4. News Filter (Mountain Time)")]
public TimeSpan S_News2End { get; set; }

[NinjaScriptProperty]
[Display(Name="News 3 start (11:55 = 2:00 EST)", Order=8, GroupName="4. News Filter (Mountain Time)")]
public TimeSpan S_News3Start { get; set; }

[NinjaScriptProperty]
[Display(Name="News 3 end (12:05 = 2:00 EST)", Order=9, GroupName="4. News Filter (Mountain Time)")]
public TimeSpan S_News3End { get; set; }

[NinjaScriptProperty]
[Display(Name="Level 1 Active", Order=1, GroupName="5. ES Levels")]
public bool S_UseL1 { get; set; }

[NinjaScriptProperty]
[Display(Name="Level 1 Price", Order=2, GroupName="5. ES Levels")]
public double S_L1 { get; set; }

[NinjaScriptProperty]
[Display(Name="Level 2 Active", Order=3, GroupName="5. ES Levels")]
public bool S_UseL2 { get; set; }

[NinjaScriptProperty]
[Display(Name="Level 2 Price", Order=4, GroupName="5. ES Levels")]
public double S_L2 { get; set; }

[NinjaScriptProperty]
[Display(Name="Level 3 Active", Order=5, GroupName="5. ES Levels")]
public bool S_UseL3 { get; set; }

[NinjaScriptProperty]
[Display(Name="Level 3 Price", Order=6, GroupName="5. ES Levels")]
public double S_L3 { get; set; }

[NinjaScriptProperty]
[Display(Name="Level 4 Active", Order=7, GroupName="5. ES Levels")]
public bool S_UseL4 { get; set; }

[NinjaScriptProperty]
[Display(Name="Level 4 Price", Order=8, GroupName="5. ES Levels")]
public double S_L4 { get; set; }

[NinjaScriptProperty]
[Display(Name="Level 5 Active", Order=9, GroupName="5. ES Levels")]
public bool S_UseL5 { get; set; }

[NinjaScriptProperty]
[Display(Name="Level 5 Price", Order=10, GroupName="5. ES Levels")]
public double S_L5 { get; set; }

[NinjaScriptProperty]
[Display(Name="Level 6 Active", Order=11, GroupName="5. ES Levels")]
public bool S_UseL6 { get; set; }

[NinjaScriptProperty]
[Display(Name="Level 6 Price", Order=12, GroupName="5. ES Levels")]
public double S_L6 { get; set; }

[NinjaScriptProperty]
[Display(Name="Level 7 Active", Order=13, GroupName="5. ES Levels")]
public bool S_UseL7 { get; set; }

[NinjaScriptProperty]
[Display(Name="Level 7 Price", Order=14, GroupName="5. ES Levels")]
public double S_L7 { get; set; }

[NinjaScriptProperty]
[Display(Name="Level 8 Active", Order=15, GroupName="5. ES Levels")]
public bool S_UseL8 { get; set; }

[NinjaScriptProperty]
[Display(Name="Level 8 Price", Order=16, GroupName="5. ES Levels")]
public double S_L8 { get; set; }

[NinjaScriptProperty]
[Display(Name="SMA20 Active", Order=17, GroupName="5. ES Levels")]
public bool S_UseSMA { get; set; }

[NinjaScriptProperty]
[Display(Name="Show Arrows", Order=1, GroupName="6. Visuals")]
public bool S_Arrows { get; set; }
}
}
