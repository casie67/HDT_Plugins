using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows.Controls;
using HearthMirror;
using HearthMirror.Objects;
using Hearthstone_Deck_Tracker;
using Hearthstone_Deck_Tracker.Controls.Overlay.Battlegrounds.Session;
using Hearthstone_Deck_Tracker.Hearthstone;
using Hearthstone_Deck_Tracker.Plugins;
using Hearthstone_Deck_Tracker.Utility.Battlegrounds;

namespace HDT_BGSessionFilter
{
    public class BGSessionFilterPlugin : IPlugin
    {
        private MenuItem _menuItem;
        private DateTime _lastUpdate = DateTime.MinValue;

        private const double IntervalSeconds = 0.2;

        private const double SessionGapHours = 2.0;

        private const int MaxPlausibleDelta = 500;

        private bool _hooked;
        private bool _writing;

        private int _writeCount;
        private DateTime _writeWindow = DateTime.MinValue;
        private const int MaxWritesPerSecond = 500;

        private string _playerId;
        private DateTime _lastPlayerIdTry = DateTime.MinValue;
        private const double PlayerIdRetrySeconds = 30.0;

        public MenuItem MenuItem
        {
            get { return _menuItem; }
        }

        public string Name
        {
            get { return "全绿战绩器"; }
        }

        public string Description
        {
            get
            {
                return "从源头删除酒馆战棋掉分的对局, 并按整个会话的正分总和反推起始分。"
                    + "只改 HDT 本地记录, 不影响暴雪服务器上的真实分数。";
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
            get { return "立即刷新"; }
        }

        public void OnLoad()
        {
            _menuItem = new MenuItem();
            _menuItem.Header = "全绿战绩器";
            _menuItem.IsCheckable = true;
            _menuItem.IsChecked = true;
        }

        public void OnUnload()
        {
            if (_menuItem != null)
            {
                _menuItem.IsChecked = false;
            }
        }

        public void OnButtonPress()
        {
            Apply();
        }

        public void OnUpdate()
        {
            if (_menuItem == null || !_menuItem.IsChecked)
            {
                return;
            }
            if ((DateTime.Now - _lastUpdate).TotalSeconds < IntervalSeconds)
            {
                return;
            }
            _lastUpdate = DateTime.Now;
            Apply();
        }

        private void Apply()
        {
            try
            {
                GameV2 game = Core.Game;
                if (game == null)
                {
                    return;
                }

                BattlegroundsSessionViewModel vm = game.BattlegroundsSessionViewModel;
                if (vm == null)
                {
                    return;
                }

                BattlegroundsLastGames store = BattlegroundsLastGames.Instance;
                if (store == null || store.Games == null)
                {
                    return;
                }

                Hook(vm);

                PurgeUi(vm);

                if (game.Spectator)
                {
                    return;
                }

                string playerId = GetPlayerId();

                bool duos = IsDuos(vm, game, store, playerId);

                PurgeLosses(store, playerId, duos);

                WriteStart(vm, game, store, playerId, duos, true);
            }
            catch (Exception)
            {
            }
        }

        private void Hook(BattlegroundsSessionViewModel vm)
        {
            if (_hooked)
            {
                return;
            }
            INotifyPropertyChanged inpc = vm as INotifyPropertyChanged;
            if (inpc == null)
            {
                return;
            }
            inpc.PropertyChanged += OnVmPropertyChanged;
            _hooked = true;
        }

        private void OnVmPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_writing)
            {
                return;
            }
            if (e == null || e.PropertyName != "BgRatingStart")
            {
                return;
            }
            if (_menuItem == null || !_menuItem.IsChecked)
            {
                return;
            }

            try
            {
                BattlegroundsSessionViewModel vm = sender as BattlegroundsSessionViewModel;
                if (vm == null)
                {
                    return;
                }

                GameV2 game = Core.Game;
                if (game == null || game.Spectator)
                {
                    return;
                }

                BattlegroundsLastGames store = BattlegroundsLastGames.Instance;
                if (store == null || store.Games == null)
                {
                    return;
                }

                string playerId = GetPlayerId();
                bool duos = IsDuos(vm, game, store, playerId);
                WriteStart(vm, game, store, playerId, duos, false);
            }
            catch (Exception)
            {
            }
        }

        private void WriteStart(BattlegroundsSessionViewModel vm, GameV2 game,
            BattlegroundsLastGames store, string playerId, bool duos,
            bool allowDisplayedCurrent)
        {
            int current;
            if (!TryGetCurrentRating(vm, game, duos, allowDisplayedCurrent, out current))
            {
                return;
            }

            List<BattlegroundsLastGames.GameItem> session =
                GetSessionGames(SortedGroup(store, playerId, duos), ClientRating(game, duos));

            int sum = 0;
            for (int i = 0; i < session.Count; i++)
            {
                sum += DisplayedDelta(session[i]);
            }

            string text = Format(current - sum);
            if (vm.BgRatingStart == text)
            {
                return;
            }

            if ((DateTime.Now - _writeWindow).TotalSeconds >= 1.0)
            {
                _writeWindow = DateTime.Now;
                _writeCount = 0;
            }
            _writeCount++;
            if (_writeCount > MaxWritesPerSecond)
            {
                return;
            }

            _writing = true;
            try
            {
                vm.BgRatingStart = text;
            }
            finally
            {
                _writing = false;
            }
        }

        private static void PurgeUi(BattlegroundsSessionViewModel vm)
        {
            var uiGames = vm.SessionGames;
            if (uiGames == null || uiGames.Count == 0)
            {
                return;
            }

            List<BattlegroundsGameViewModel> stale =
                uiGames.Where(g => g != null && g.MMRDelta < 0).ToList();
            for (int i = 0; i < stale.Count; i++)
            {
                uiGames.Remove(stale[i]);
            }
        }

        private static void PurgeLosses(BattlegroundsLastGames store, string playerId, bool duos)
        {
            List<BattlegroundsLastGames.GameItem> group = SortedGroup(store, playerId, duos);
            if (group.Count == 0)
            {
                return;
            }

            List<BattlegroundsLastGames.GameItem> losses =
                group.Where(IsLoss).ToList();
            if (losses.Count == 0)
            {
                return;
            }

            for (int i = 0; i < losses.Count; i++)
            {
                string startTime = losses[i].StartTime;
                if (!string.IsNullOrEmpty(startTime))
                {
                    store.RemoveGame(startTime, false);
                }
            }
        }

        private static bool IsLoss(BattlegroundsLastGames.GameItem g)
        {
            if (g == null || g.FriendlyGame || !g.RatingAfter.HasValue)
            {
                return false;
            }
            return Delta(g) < 0;
        }

        private static List<BattlegroundsLastGames.GameItem> GetSessionGames(
            List<BattlegroundsLastGames.GameItem> sortedGames, int? clientRating)
        {
            DateTime? sessionStart = null;
            DateTime? prevEnd = null;
            int before = 0;

            for (int i = 0; i < sortedGames.Count; i++)
            {
                BattlegroundsLastGames.GameItem g = sortedGames[i];
                DateTime start;
                if (!DateTime.TryParse(g.StartTime, out start))
                {
                    continue;
                }

                if (prevEnd.HasValue)
                {
                    bool reset = BattlegroundsLastGames.IsRatingReset(before, g.Rating);
                    if ((start - prevEnd.Value).TotalHours >= SessionGapHours || reset)
                    {
                        sessionStart = start;
                    }
                }

                DateTime end;
                if (DateTime.TryParse(g.EndTime, out end))
                {
                    prevEnd = end;
                }
                before = g.RatingAfterOrCarriedForward;
            }

            List<BattlegroundsLastGames.GameItem> list;
            if (!sessionStart.HasValue)
            {
                list = sortedGames;
            }
            else
            {
                DateTime cutoff = sessionStart.Value;
                list = sortedGames.Where(delegate(BattlegroundsLastGames.GameItem g)
                {
                    DateTime v;
                    return DateTime.TryParse(g.StartTime, out v) && v >= cutoff;
                }).ToList();
            }

            if (list.Count > 0)
            {
                BattlegroundsLastGames.GameItem last = list[list.Count - 1];
                bool wasReset = false;
                if (clientRating.HasValue)
                {
                    wasReset = BattlegroundsLastGames.IsRatingReset(
                        last.RatingAfterOrCarriedForward, clientRating.Value);
                }

                DateTime lastEnd;
                if (DateTime.TryParse(last.EndTime, out lastEnd)
                    && ((DateTime.Now - lastEnd).TotalHours >= SessionGapHours || wasReset))
                {
                    return new List<BattlegroundsLastGames.GameItem>();
                }
            }

            return list;
        }

        private string GetPlayerId()
        {
            if (_playerId != null)
            {
                return _playerId;
            }
            if ((DateTime.Now - _lastPlayerIdTry).TotalSeconds < PlayerIdRetrySeconds)
            {
                return null;
            }
            _lastPlayerIdTry = DateTime.Now;

            try
            {
                AccountId accountId = Reflection.Client.GetAccountId();
                if (accountId != null)
                {
                    _playerId = accountId.Hi + "_" + accountId.Lo;
                }
            }
            catch (Exception)
            {
            }
            return _playerId;
        }

        private static List<BattlegroundsLastGames.GameItem> SortedGroup(
            BattlegroundsLastGames store, string playerId, bool duos)
        {
            return store.Games
                .Where(g => g != null
                    && (playerId == null || g.Player == null || g.Player == playerId)
                    && g.Duos == duos)
                .OrderBy(g => g.StartTime)
                .ToList();
        }

        private static bool IsDuos(BattlegroundsSessionViewModel vm, GameV2 game,
            BattlegroundsLastGames store, string playerId)
        {
            if (!game.IsInMenu)
            {
                return game.IsBattlegroundsDuosMatch;
            }

            try
            {
                var mode = vm.BattlegroundsGameMode;
                string name = mode.ToString();
                if (name == "DUOS")
                {
                    return true;
                }
                if (name == "SOLOS")
                {
                    return false;
                }
            }
            catch (Exception)
            {
            }

            BattlegroundsLastGames.GameItem newest = store.Games
                .Where(g => g != null
                    && (playerId == null || g.Player == null || g.Player == playerId))
                .OrderBy(g => g.StartTime)
                .LastOrDefault();
            return newest != null && newest.Duos;
        }

        private static int Delta(BattlegroundsLastGames.GameItem g)
        {
            if (!g.RatingAfter.HasValue)
            {
                return 0;
            }
            int after = g.RatingAfter.Value;
            return g.SeasonReset ? after : (after - g.Rating);
        }

        private static int DisplayedDelta(BattlegroundsLastGames.GameItem g)
        {
            if (!g.RatingAfter.HasValue || g.FriendlyGame)
            {
                return 0;
            }
            int d = Delta(g);
            if (Math.Abs(d) > MaxPlausibleDelta)
            {
                return 0;
            }
            return d;
        }

        private static int? ClientRating(GameV2 game, bool duos)
        {
            var info = game.BattlegroundsRatingInfo;
            if (info == null)
            {
                return null;
            }
            return duos ? info.DuosRating : info.Rating;
        }

        private static bool TryGetCurrentRating(BattlegroundsSessionViewModel vm,
            GameV2 game, bool duos, bool allowDisplayed, out int rating)
        {
            rating = 0;
            if (allowDisplayed && TryParseRating(vm.BgRatingCurrent, out rating))
            {
                return true;
            }

            int? clientRating = game.CurrentBattlegroundsRating;
            if (clientRating.HasValue && clientRating.Value > 0)
            {
                rating = clientRating.Value;
                return true;
            }

            int? fromInfo = ClientRating(game, duos);
            if (fromInfo.HasValue && fromInfo.Value > 0)
            {
                rating = fromInfo.Value;
                return true;
            }

            rating = 0;
            return false;
        }

        private static bool TryParseRating(string text, out int value)
        {
            value = 0;
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            StringBuilder sb = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                if (char.IsDigit(c) || (c == '-' && sb.Length == 0))
                {
                    sb.Append(c);
                }
            }

            if (sb.Length == 0)
            {
                return false;
            }

            return int.TryParse(sb.ToString(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out value);
        }

        private static string Format(int value)
        {
            return value.ToString("N0", CultureInfo.CurrentCulture);
        }
    }
}
