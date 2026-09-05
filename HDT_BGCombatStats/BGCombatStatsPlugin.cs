using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HearthDb.Enums;
using Hearthstone_Deck_Tracker;
using Hearthstone_Deck_Tracker.BobsBuddy;
using Hearthstone_Deck_Tracker.Controls.Overlay;
using Hearthstone_Deck_Tracker.Hearthstone;
using Hearthstone_Deck_Tracker.Hearthstone.Entities;
using Hearthstone_Deck_Tracker.Plugins;
using Hearthstone_Deck_Tracker.Utility.Extensions;
using ApiCore = Hearthstone_Deck_Tracker.API.Core;

namespace HDT_BGCombatStats
{
    public class BGCombatStatsPlugin : IPlugin
    {
        private const int NotFound = int.MinValue;
        private const int ZonePlay = 1;
        private const double IntervalSeconds = 0.2;
        private const double BeforeResampleSeconds = 1.0;
        private const double SettleDelaySeconds = 1.5;

        private MenuItem _menuItem;
        private DateTime _lastUpdate = DateTime.MinValue;
        private BobsBuddyPanel _panel;

        private int _phase;
        private DateTime _combatStart;
        private DateTime _combatEnd;
        private bool _resampledBefore;

        private int _turn;
        private int _myHeroId;
        private string _oppCardId;
        private string _oppName;
        private int _myB0, _myB1, _oppB0, _oppB1;
        private int _myLast, _oppLast;
        private string _predWin, _predTie, _predLoss;
        private string _predKillOpp, _predKillMe;
        private bool _sawReconnect;

        private double _sumWin, _sumTie, _sumLoss;
        private int _cntWin, _cntTie, _cntLoss;
        private double _sumKillOpp, _sumKillMe;
        private int _cntKillOpp, _cntKillMe;
        private int _skipped;
        private int _games;
        private int _lastTurnSeen;
        private bool _pendingNewGame = true;

        private Border _root;
        private StackPanel _stack;
        private TextBlock _line1, _line2, _line3, _line4;
        private ScaleTransform _scale;
        private bool _dragging;
        private Point _dragMouse;
        private Point _dragOrigin;
        private bool _placed;

        public MenuItem MenuItem
        {
            get { return _menuItem; }
        }

        public string Name
        {
            get { return "狗运计算器"; }
        }

        public string Description
        {
            get
            {
                return "酒馆战棋逐轮战斗统计: 累加 BobsBuddy 预测概率, 并统计实际胜平负次数 "
                    + "(拔线也能判定)。悬浮窗可拖动, 滚轮缩放。按钮重置统计。";
            }
        }

        public string Author
        {
            get { return ""; }
        }

        public Version Version
        {
            get { return new Version(1, 0, 0); }
        }

        public string ButtonText
        {
            get { return "重置统计"; }
        }

        public void OnLoad()
        {
            _menuItem = new MenuItem();
            _menuItem.Header = "狗运计算器";
            _menuItem.IsCheckable = true;
            _menuItem.IsChecked = true;
            BuildUi();
            Refresh();
        }

        public void OnUnload()
        {
            if (_menuItem != null)
            {
                _menuItem.IsChecked = false;
            }
            try
            {
                if (_root != null && ApiCore.OverlayCanvas != null)
                {
                    ApiCore.OverlayCanvas.Children.Remove(_root);
                }
            }
            catch (Exception)
            {
            }
            _root = null;
        }

        public void OnButtonPress()
        {
            _sumWin = 0;
            _sumTie = 0;
            _sumLoss = 0;
            _cntWin = 0;
            _cntTie = 0;
            _cntLoss = 0;
            _sumKillOpp = 0;
            _sumKillMe = 0;
            _cntKillOpp = 0;
            _cntKillMe = 0;
            _skipped = 0;
            _games = 0;
            _pendingNewGame = true;
            Log("=== 统计已重置 " + DateTime.Now.ToString("HH:mm:ss") + " ===");
            Refresh();
        }

        public void OnUpdate()
        {
            if (_menuItem == null || !_menuItem.IsChecked)
            {
                if (_root != null)
                {
                    _root.Visibility = Visibility.Collapsed;
                }
                return;
            }
            if ((DateTime.Now - _lastUpdate).TotalSeconds < IntervalSeconds)
            {
                return;
            }
            _lastUpdate = DateTime.Now;

            try
            {
                Tick();
                UpdateVisibility();
            }
            catch (Exception ex)
            {
                Log("[异常] " + ex.Message);
            }
        }

        private void Tick()
        {
            GameV2 game = Core.Game;
            if (game == null)
            {
                return;
            }
            BobsBuddyPanel panel = Panel();
            if (panel == null)
            {
                return;
            }

            BobsBuddyState state = panel.State;
            bool combat = IsCombatState(state);

            if (game.IsReconnect || game.AfterReconnect)
            {
                _sawReconnect = true;
            }

            if (_phase == 2)
            {
                double elapsed = (DateTime.Now - _combatEnd).TotalSeconds;
                int myNow = TotalById(game, _myHeroId);
                int oppNow = Total(FindPersistentHero(game, _oppCardId));
                if (myNow != NotFound)
                {
                    _myLast = myNow;
                }
                if (oppNow != NotFound)
                {
                    _oppLast = oppNow;
                }

                if (combat || elapsed >= SettleDelaySeconds)
                {
                    Settle();
                    _phase = 0;
                }
                if (!combat)
                {
                    return;
                }
            }

            if (combat)
            {
                if (_phase != 1)
                {
                    _phase = 1;
                    _combatStart = DateTime.Now;
                    _resampledBefore = false;

                    _turn = game.GetTurnNumber();
                    _myHeroId = HeroId(game.Player);

                    Entity oppPlayCopy = HeroOf(game.Opponent);
                    _oppCardId = (oppPlayCopy == null) ? null : oppPlayCopy.CardId;
                    _oppName = (oppPlayCopy == null) ? null : oppPlayCopy.LocalizedName;

                    _myB0 = TotalById(game, _myHeroId);
                    _oppB0 = Total(FindPersistentHero(game, _oppCardId));
                    _myB1 = NotFound;
                    _oppB1 = NotFound;
                    _myLast = NotFound;
                    _oppLast = NotFound;

                    _predWin = null;
                    _predTie = null;
                    _predLoss = null;
                    _predKillOpp = null;
                    _predKillMe = null;
                    _sawReconnect = game.IsReconnect || game.AfterReconnect;

                    if (_turn <= _lastTurnSeen)
                    {
                        _pendingNewGame = true;
                    }
                    _lastTurnSeen = _turn;
                }

                if (!_resampledBefore
                    && (DateTime.Now - _combatStart).TotalSeconds >= BeforeResampleSeconds)
                {
                    _resampledBefore = true;
                    _myB1 = TotalById(game, _myHeroId);
                    _oppB1 = Total(FindPersistentHero(game, _oppCardId));
                }

                string w = panel.WinRateDisplay;
                string t = panel.TieRateDisplay;
                string l = panel.LossRateDisplay;
                if (!IsBlank(w) || !IsBlank(t) || !IsBlank(l))
                {
                    _predWin = w;
                    _predTie = t;
                    _predLoss = l;
                    _predKillOpp = panel.PlayerLethalDisplay;
                    _predKillMe = panel.OpponentLethalDisplay;
                }
                return;
            }

            if (_phase == 1)
            {
                _phase = 2;
                _combatEnd = DateTime.Now;
                _myLast = TotalById(game, _myHeroId);
                _oppLast = Total(FindPersistentHero(game, _oppCardId));
            }
        }

        private void Settle()
        {
            int myBefore = (_myB1 != NotFound) ? _myB1 : _myB0;
            int oppBefore = (_oppB1 != NotFound) ? _oppB1 : _oppB0;
            bool oppUsable = (oppBefore != NotFound && oppBefore > 0);

            string verdict = oppUsable
                ? Judge(myBefore, _myLast, oppBefore, _oppLast)
                : null;

            double pw, pt, pl;
            bool hasPred = TryPct(_predWin, out pw) & TryPct(_predTie, out pt)
                & TryPct(_predLoss, out pl);

            string note;
            if (!oppUsable)
            {
                note = "跳过(对手血量无效, 尸体对手)";
                _skipped++;
            }
            else if (verdict == null)
            {
                note = "跳过(判定不出)";
                _skipped++;
            }
            else if (!hasPred)
            {
                note = "跳过(无预测值)";
                _skipped++;
            }
            else
            {
                if (_pendingNewGame)
                {
                    _games++;
                    _pendingNewGame = false;
                }
                _sumWin += pw;
                _sumTie += pt;
                _sumLoss += pl;

                double ko, km;
                if (!TryPct(_predKillOpp, out ko))
                {
                    ko = 0;
                }
                if (!TryPct(_predKillMe, out km))
                {
                    km = 0;
                }
                _sumKillOpp += ko;
                _sumKillMe += km;

                if (_oppLast != NotFound && _oppLast <= 0 && oppBefore > 0)
                {
                    _cntKillOpp++;
                }
                if (_myLast != NotFound && _myLast <= 0 && myBefore > 0)
                {
                    _cntKillMe++;
                }
                if (verdict == "胜")
                {
                    _cntWin++;
                }
                else if (verdict == "平")
                {
                    _cntTie++;
                }
                else
                {
                    _cntLoss++;
                }
                note = verdict;
            }

            StringBuilder sb = new StringBuilder();
            sb.Append("第").Append(_turn).Append("回合  VS ").Append(_oppName ?? "?");
            sb.Append("  预测 ").Append(Fmt(_predWin)).Append("/")
              .Append(Fmt(_predTie)).Append("/").Append(Fmt(_predLoss));
            sb.Append("  我方 ").Append(N(myBefore)).Append("->").Append(N(_myLast));
            sb.Append("  对手 ").Append(N(oppBefore)).Append("->").Append(N(_oppLast));
            sb.Append("  淘汰预测 ").Append(Fmt(_predKillOpp)).Append("/").Append(Fmt(_predKillMe));
            sb.Append("  ").Append(note);
            if (_sawReconnect)
            {
                sb.Append("  [拔线]");
            }
            Log(sb.ToString());

            Refresh();
        }

        private static string Judge(int myBefore, int myAfter, int oppBefore, int oppAfter)
        {
            if (myBefore == NotFound || myAfter == NotFound)
            {
                return null;
            }
            int myDrop = myBefore - myAfter;
            if (oppBefore == NotFound || oppAfter == NotFound)
            {
                return null;
            }
            int oppDrop = oppBefore - oppAfter;
            if (myDrop > 0 && oppDrop > 0)
            {
                return null;
            }
            if (myDrop > 0)
            {
                return "负";
            }
            if (oppDrop > 0)
            {
                return "胜";
            }
            return "平";
        }

        private void BuildUi()
        {
            try
            {
                _scale = new ScaleTransform(1.0, 1.0);

                _line1 = MakeText(Colors.White, 15, true);
                _line2 = MakeText(Color.FromRgb(158, 200, 235), 14, false);
                _line3 = MakeText(Color.FromRgb(158, 235, 170), 14, false);
                _line4 = MakeText(Color.FromRgb(230, 190, 150), 14, false);

                _stack = new StackPanel();
                _stack.Orientation = Orientation.Vertical;
                _stack.Children.Add(_line1);
                _stack.Children.Add(_line2);
                _stack.Children.Add(_line3);
                _stack.Children.Add(_line4);

                _root = new Border();
                _root.Background = new SolidColorBrush(Color.FromArgb(216, 24, 24, 26));
                _root.BorderBrush = new SolidColorBrush(Color.FromRgb(80, 80, 88));
                _root.BorderThickness = new Thickness(1);
                _root.CornerRadius = new CornerRadius(3);
                _root.Padding = new Thickness(8, 5, 8, 5);
                _root.Child = _stack;
                _root.RenderTransform = _scale;
                _root.Visibility = Visibility.Collapsed;
                _root.Cursor = Cursors.SizeAll;
                _root.ToolTip = "拖动移动, 滚轮缩放";

                _root.MouseLeftButtonDown += OnDragStart;
                _root.MouseLeftButtonUp += OnDragEnd;
                _root.MouseMove += OnDragMove;
                _root.MouseWheel += OnWheel;
                _root.LostMouseCapture += OnLostCapture;

                if (ApiCore.OverlayCanvas != null)
                {
                    ApiCore.OverlayCanvas.Children.Add(_root);
                }
                OverlayExtensions.SetIsOverlayHitTestVisible(_root, true);
            }
            catch (Exception ex)
            {
                Log("[建界面失败] " + ex.Message);
            }
        }

        private static TextBlock MakeText(Color c, double size, bool bold)
        {
            TextBlock tb = new TextBlock();
            tb.Foreground = new SolidColorBrush(c);
            tb.FontSize = size;
            tb.FontFamily = new FontFamily("Consolas, Microsoft YaHei");
            if (bold)
            {
                tb.FontWeight = FontWeights.Bold;
            }
            return tb;
        }

        private void Refresh()
        {
            if (_line1 == null)
            {
                return;
            }
            int rounds = _cntWin + _cntTie + _cntLoss;

            _line1.Text = "本次统计   " + _games + " 局 / " + rounds + " 轮"
                + ((_skipped > 0) ? ("   跳过 " + _skipped + " 轮") : "");
            _line2.Text = "预测  " + P(_sumWin) + " : " + P(_sumTie) + " : " + P(_sumLoss);
            _line3.Text = "实际  " + _cntWin + " 胜 : " + _cntTie + " 平 : " + _cntLoss + " 负";
            _line4.Text = "淘汰  预测 " + P(_sumKillOpp) + " 对面 / " + P(_sumKillMe)
                + " 自己   实际 " + _cntKillOpp + " / " + _cntKillMe;

            if (rounds == 0 && _skipped == 0)
            {
                _line1.Text = "等待第一轮战斗";
            }
        }

        private static string P(double v)
        {
            return v.ToString("0.#", CultureInfo.InvariantCulture) + "%";
        }

        private void UpdateVisibility()
        {
            if (_root == null)
            {
                return;
            }
            GameV2 game = Core.Game;
            bool show = false;
            try
            {
                show = (game != null && game.IsBattlegroundsMatch)
                    || (_cntWin + _cntTie + _cntLoss) > 0;
            }
            catch (Exception)
            {
            }
            _root.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

            if (show && !_placed)
            {
                _placed = true;
                Place();
            }
        }

        private void Place()
        {
            double left = 20;
            double top = 220;
            try
            {
                BobsBuddyPanel p = Panel();
                if (p != null && ApiCore.OverlayCanvas != null && p.IsVisible)
                {
                    Point pos = p.TransformToAncestor(ApiCore.OverlayCanvas)
                        .Transform(new Point(0, 0));
                    left = pos.X;
                    top = pos.Y + p.ActualHeight + 6;
                }
                double ratio = ApiCore.OverlayWindow.Width / 1920.0;
                if (ratio < 0.5)
                {
                    ratio = 0.5;
                }
                if (ratio > 2.0)
                {
                    ratio = 2.0;
                }
                _scale.ScaleX = ratio;
                _scale.ScaleY = ratio;
            }
            catch (Exception)
            {
            }
            Canvas.SetLeft(_root, left);
            Canvas.SetTop(_root, top);
        }

        private void OnDragStart(object sender, MouseButtonEventArgs e)
        {
            _root.CaptureMouse();
            _dragging = true;
            _dragMouse = e.GetPosition(ApiCore.OverlayCanvas);
            _dragOrigin = new Point(Canvas.GetLeft(_root), Canvas.GetTop(_root));
            if (double.IsNaN(_dragOrigin.X))
            {
                _dragOrigin = new Point(0, 0);
            }
        }

        private void OnDragEnd(object sender, MouseEventArgs e)
        {
            _dragging = false;
            if (_root != null && _root.IsMouseCaptured)
            {
                _root.ReleaseMouseCapture();
            }
        }

        private void OnLostCapture(object sender, MouseEventArgs e)
        {
            _dragging = false;
        }

        private void OnDragMove(object sender, MouseEventArgs e)
        {
            if (!_dragging)
            {
                return;
            }
            Point now = e.GetPosition(ApiCore.OverlayCanvas);
            double left = _dragOrigin.X + (now.X - _dragMouse.X);
            double top = _dragOrigin.Y + (now.Y - _dragMouse.Y);
            if (left < 0)
            {
                left = 0;
            }
            if (top < 0)
            {
                top = 0;
            }
            Canvas.SetLeft(_root, left);
            Canvas.SetTop(_root, top);
        }

        private void OnWheel(object sender, MouseWheelEventArgs e)
        {
            double r = _scale.ScaleX + ((e.Delta > 0) ? 0.1 : -0.1);
            if (r < 0.5)
            {
                r = 0.5;
            }
            if (r > 2.0)
            {
                r = 2.0;
            }
            _scale.ScaleX = r;
            _scale.ScaleY = r;
        }

        private static bool IsCombatState(BobsBuddyState s)
        {
            return s == BobsBuddyState.Combat
                || s == BobsBuddyState.CombatPartial
                || s == BobsBuddyState.CombatWithoutSimulation;
        }

        private static Entity HeroOf(Player p)
        {
            return (p == null) ? null : p.Hero;
        }

        private static int HeroId(Player p)
        {
            Entity h = HeroOf(p);
            return (h == null) ? 0 : h.Id;
        }

        private static int Total(Entity h)
        {
            if (h == null)
            {
                return NotFound;
            }
            return h.GetTag(GameTag.HEALTH) - h.GetTag(GameTag.DAMAGE) + h.GetTag(GameTag.ARMOR);
        }

        private static int TotalById(GameV2 game, int entityId)
        {
            if (game == null || entityId == 0 || game.Entities == null)
            {
                return NotFound;
            }
            Entity e;
            if (game.Entities.TryGetValue(entityId, out e))
            {
                return Total(e);
            }
            return NotFound;
        }

        private static Entity FindPersistentHero(GameV2 game, string cardId)
        {
            if (game == null || game.Entities == null || string.IsNullOrEmpty(cardId))
            {
                return null;
            }
            Entity best = null;
            bool bestIsPlay = true;
            foreach (Entity e in game.Entities.Values)
            {
                if (e == null || !e.IsHero || e.CardId != cardId)
                {
                    continue;
                }
                bool isPlay = e.GetTag(GameTag.ZONE) == ZonePlay;
                if (best == null || (bestIsPlay && !isPlay)
                    || (bestIsPlay == isPlay && e.Id < best.Id))
                {
                    best = e;
                    bestIsPlay = isPlay;
                }
            }
            return best;
        }

        private static bool TryPct(string s, out double value)
        {
            value = 0;
            if (IsBlank(s))
            {
                return false;
            }
            StringBuilder sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (char.IsDigit(c) || c == '.')
                {
                    sb.Append(c);
                }
            }
            if (sb.Length == 0)
            {
                return false;
            }
            return double.TryParse(sb.ToString(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out value);
        }

        private static bool IsBlank(string s)
        {
            return string.IsNullOrEmpty(s) || s == "-";
        }

        private static string Fmt(string s)
        {
            return IsBlank(s) ? "?" : s;
        }

        private static string N(int v)
        {
            return (v == NotFound) ? "x" : v.ToString(CultureInfo.InvariantCulture);
        }

        private BobsBuddyPanel Panel()
        {
            if (_panel != null)
            {
                return _panel;
            }
            try
            {
                _panel = FindChild(Core.Overlay);
            }
            catch (Exception)
            {
            }
            return _panel;
        }

        private static BobsBuddyPanel FindChild(DependencyObject root)
        {
            if (root == null)
            {
                return null;
            }
            BobsBuddyPanel hit = root as BobsBuddyPanel;
            if (hit != null)
            {
                return hit;
            }
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                BobsBuddyPanel found = FindChild(VisualTreeHelper.GetChild(root, i));
                if (found != null)
                {
                    return found;
                }
            }
            return null;
        }

        private static void Log(string line)
        {
            try
            {
                string dir = Path.Combine(Config.AppDataPath, "BGCombatStats");
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                string file = Path.Combine(dir,
                    DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".txt");
                File.AppendAllLines(file, new List<string> { line });
            }
            catch (Exception)
            {
            }
        }
    }
}
