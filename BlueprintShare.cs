using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Facepunch;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Game.Rust;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Blueprint Share", "c_creep", "1.4.3")]
    [Description("Allows players to share researched blueprints with their friends, clan or team")]
    class BlueprintShare : RustPlugin
    {
        #region Fields

        [PluginReference]
        private Plugin Clans, Friends;

        private StoredData storedData;

        private Dictionary<string, Action<BasePlayer, string[]>> chatCommandHandlers;
        private Dictionary<ulong, List<ulong>> shareTargetCache = new Dictionary<ulong, List<ulong>>();
        private Dictionary<ulong, Timer> shareTargetCacheTimers = new Dictionary<ulong, Timer>();

        private const float shareTargetCacheDuration = 10f; // Seconds

        private enum ShareType
        {
            Teams,
            Friends,
            Clans
        }

        private enum DebugLevel
        {
            Info,
            Warning,
            Error
        }

        private const string usePermission = "blueprintshare.use";
        private const string togglePermission = "blueprintshare.toggle";
        private const string sharePermission = "blueprintshare.share";
        private const string showPermission = "blueprintshare.show";
        private const string bypassPermission = "blueprintshare.bypass";

        private bool isTeamsEnabled;
        private bool isClansEnabled;
        private bool isFriendsEnabled;
        private bool dataDirty = false;

        #endregion

        #region Oxide Hooks

        private void Init()
        {
            LoadData();

            permission.RegisterPermission(usePermission, this);
            permission.RegisterPermission(togglePermission, this);
            permission.RegisterPermission(sharePermission, this);
            permission.RegisterPermission(showPermission, this);
            permission.RegisterPermission(bypassPermission, this);

            isTeamsEnabled = config.TeamsEnabled;
            isClansEnabled = config.ClansEnabled;
            isFriendsEnabled = config.FriendsEnabled;

            chatCommandHandlers = new Dictionary<string, Action<BasePlayer, string[]>>
            {
                ["help"] = HelpCommand,
                ["toggle"] = ToggleCommand,
                ["share"] = ShareCommand,
                ["show"] = ShowCommand
            };

            if (!isTeamsEnabled)
            {
                Unsubscribe(nameof(OnTeamAcceptInvite));
                Unsubscribe(nameof(OnTeamKick));
                Unsubscribe(nameof(OnTeamLeave));
            }

            if (!isClansEnabled)
            {
                Unsubscribe(nameof(OnClanMemberJoined));
                Unsubscribe(nameof(OnClanMemberGone));
                Unsubscribe(nameof(OnClanDisbanded));
            }

            if (!isFriendsEnabled)
            {
                Unsubscribe(nameof(OnFriendAdded));
                Unsubscribe(nameof(OnFriendRemoved));
            }
        }

        private void OnNewSave(string filename)
        {
            if (!config.ClearDataOnWipe) return;

            CreateData();
        }

        private void OnServerShutdown()
        {
            if (dataDirty)
            {
                SaveData();
            }
        }

        private void OnServerSave()
        {
            if (dataDirty)
            {
                SaveData();
            }
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (dataDirty)
            {
                SaveData();
            }
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            GetPlayerData(player.UserIDString);
        }

        private void OnItemAction(Item item, string action, BasePlayer player)
        {
            if (!config.ItemSharingEnabled) return;
            if (player == null || item == null) return;
            if (action != "study") return;

            DebugLog($"{player.displayName} Started Blueprint Item Unlock For {item.blueprintTargetDef.displayName.translated}");

            if (TryShareBlueprint(item.blueprintTargetDef, player))
            {
                item.Remove();
            }
        }

        private void OnTechTreeNodeUnlocked(Workbench workbench, TechTreeData.NodeInstance node, BasePlayer player)
        {
            if (!config.TechTreeSharingEnabled || workbench == null || node == null || player == null) return;

            DebugLog($"{player.displayName} Started TechTree Unlock For {node.itemDef.displayName.translated}");

            TryShareBlueprint(node.itemDef, player);
        }

        private void OnTeamAcceptInvite(RelationshipManager.PlayerTeam team, BasePlayer joiningPlayer)
        {
            if (!config.ShareToExistingMembers && !config.ShareToNewMembers) return;
            if (team == null || joiningPlayer == null) return;

            DebugLog($"{joiningPlayer.displayName} joined a team");

            timer.Once(1f, () =>
            {
                var teamMemberIds = team.members;

                if (teamMemberIds == null || teamMemberIds.Count == 0) return;

                var teamMembers = FindPlayersByIds(teamMemberIds, joiningPlayer.userID);

                if (teamMembers.Count == 0) return;

                // Share FROM joining player TO each team member
                if (config.ShareToExistingMembers)
                {
                    foreach (var member in teamMembers)
                    {
                        if (member == null || member == joiningPlayer) continue;
                        ShareWithPlayer(joiningPlayer, member); // member receives from joiner
                    }
                }

                // Share FROM each team member TO joining player
                if (config.ShareToNewMembers)
                {
                    foreach (var member in teamMembers)
                    {
                        if (member == null || member == joiningPlayer) continue;
                        ShareWithPlayer(member, joiningPlayer); // joiner receives from member
                    }
                }
            });
        }

        private void OnTeamLeave(RelationshipManager.PlayerTeam team, BasePlayer player)
        {
            DebugLog($"{player.displayName} left their team");

            PlayerLeftTeam(player.userID);
        }

        private void OnTeamKick(RelationshipManager.PlayerTeam team, BasePlayer player, ulong targetId)
        {
            DebugLog($"{targetId} was kicked from their team by {player.displayName}");

            PlayerLeftTeam(targetId);
        }

        #endregion

        #region External Plugins

        #region Friends

        private bool HasFriends(ulong playerId)
        {
            if (Friends == null || !Friends.IsLoaded) return false;

            var friendsList = Friends.Call<ulong[]>("GetFriends", playerId);

            return friendsList != null && friendsList.Length != 0;
        }

        private List<ulong> GetFriends(ulong playerId)
        {
            if (Friends == null || !Friends.IsLoaded) return new List<ulong>();

            var friends = Friends.Call<ulong[]>("GetFriends", playerId);

            return friends.ToList();
        }

        private bool IsFriend(ulong playerId, ulong targetId)
        {
            return Friends != null && Friends.IsLoaded && Friends.Call<bool>("HasFriend", playerId, targetId);
        }

        private bool AreMutualFriends(ulong playerId, ulong targetId)
        {
            return Friends != null && Friends.IsLoaded && Friends.Call<bool>("AreFriends", playerId, targetId);
        }

        #endregion

        #region Clan

        private bool InClan(ulong playerId)
        {
            if (Clans == null) return false;

            var clanName = Clans?.Call<string>("GetClanOf", playerId);

            return clanName != null;
        }

        private List<ulong> GetClanMembers(ulong playerId)
        {
            var clanTag = Clans?.Call<string>("GetClanOf", playerId);

            if (string.IsNullOrEmpty(clanTag)) return new List<ulong>();

            var clanInfo = Clans?.Call<JObject>("GetClan", clanTag);

            if (clanInfo == null) return new List<ulong>();

            var clanMembers = clanInfo["members"];

            return clanInfo["members"] != null ? clanMembers.ToObject<List<ulong>>() : new List<ulong>();
        }

        private bool SameClan(ulong playerId, ulong targetId)
        {
            var playerClanTag = Clans?.Call<string>("GetClanOf", playerId);

            if (string.IsNullOrEmpty(playerClanTag)) return false;

            var targetClanTag = Clans?.Call<string>("GetClanOf", targetId);

            if (string.IsNullOrEmpty(targetClanTag)) return false;

            return playerClanTag == targetClanTag;
        }

        #endregion

        #region Team

        private bool InTeam(ulong playerId)
        {
            if (!RelationshipManager.TeamsEnabled()) return false;

            var playersTeam = RelationshipManager.ServerInstance.FindPlayersTeam(playerId);

            return playersTeam != null && playersTeam.members.Count > 1;
        }

        private List<ulong> GetTeamMembers(ulong playerId)
        {
            var playersTeam = RelationshipManager.ServerInstance.FindPlayersTeam(playerId);

            return playersTeam?.members;
        }

        private bool SameTeam(ulong playerId, ulong targetId)
        {
            var playerTeam = RelationshipManager.ServerInstance.FindPlayersTeam(playerId);

            if (playerTeam == null) return false;

            var targetTeam = RelationshipManager.ServerInstance.FindPlayersTeam(targetId);

            if (targetTeam == null) return false;

            return playerTeam == targetTeam;
        }

        #endregion

        #endregion

        #region External Plugin Hooks

        private void OnFriendAdded(string playerIdStr, string friendIdStr)
        {
            if (!config.ShareToExistingMembers && !config.ShareToNewMembers) return;

            if (!ulong.TryParse(playerIdStr, out var playerId) || !ulong.TryParse(friendIdStr, out var friendId)) return;

            // Only proceed if both players consider each other friends
            if (!AreMutualFriends(playerId, friendId))
            {
                DebugLog($"Not sharing blueprints between {GetPlayerName(playerId)} and {GetPlayerName(friendId)} — friendship is not mutual.");
                return;
            }

            var player = RustCore.FindPlayerById(playerId);
            var friend = RustCore.FindPlayerById(friendId);

            if (player == null || friend == null) return;

            DebugLog($"{player.displayName} added {friend.displayName} as a friend");

            // Share FROM new friend TO player
            if (config.ShareToExistingMembers)
            {
                ShareWithPlayer(friend, player); // player receives blueprints from friend
            }

            // Share FROM player TO new friend
            if (config.ShareToNewMembers)
            {
                ShareWithPlayer(player, friend); // friend receives blueprints from player
            }
        }

        private void OnFriendRemoved(string playerIdStr, string friendIdStr)
        {
            if (!config.LoseBlueprintsOnLeave) return;

            if (!ulong.TryParse(playerIdStr, out var playerId) || !ulong.TryParse(friendIdStr, out var friendId)) return;

            DebugLog($"{GetPlayerName(playerId)} has removed {GetPlayerName(friendId)} as a friend");

            var stillFriends = IsFriend(friendId, playerId);

            if (stillFriends)
            {
                DebugLog($"{GetPlayerName(playerId)} removed {GetPlayerName(friendId)}, but they are still mutual friends. No blueprints removed.");
                return;
            }

            // If not mutual anymore, remove shared blueprints in both directions
            var playerToFriend = GetSharedBlueprints(playerIdStr, ShareType.Friends, friendIdStr);
            var friendToPlayer = GetSharedBlueprints(friendIdStr, ShareType.Friends, playerIdStr);

            if (playerToFriend.Count > 0)
            {
                RemoveBlueprints(playerId, playerToFriend, ShareType.Friends, friendIdStr);
            }

            if (friendToPlayer.Count > 0)
            {
                RemoveBlueprints(friendId, friendToPlayer, ShareType.Friends, playerIdStr);
            }
        }

        private void OnClanMemberJoined(ulong playerId, string clanName)
        {
            if (!config.ShareToExistingMembers && !config.ShareToNewMembers) return;

            var player = RustCore.FindPlayerById(playerId);

            if (player == null) return;

            DebugLog($"{player.displayName} joined the {clanName} clan");

            var clanMemberIds = GetClanMembers(playerId);

            if (clanMemberIds == null || clanMemberIds.Count == 0) return;

            var clanMembers = FindPlayersByIds(clanMemberIds, playerId);

            if (clanMembers.Count == 0) return;

            foreach (var clanMember in clanMembers)
            {
                if (clanMember == null || clanMember == player) continue;

                // Share FROM joining player TO existing member
                if (config.ShareToExistingMembers)
                {
                    ShareWithPlayer(player, clanMember); // clan member receives from new player
                }

                // Share FROM existing member TO joining player
                if (config.ShareToNewMembers)
                {
                    ShareWithPlayer(clanMember, player); // new player receives from clan member
                }
            }
        }

        private void OnClanMemberGone(ulong playerId, string tag)
        {
            if (!config.LoseBlueprintsOnLeave) return;

            var learntBlueprints = GetSharedBlueprints(playerId.ToString(), ShareType.Clans);

            if (learntBlueprints.Count == 0) return;

            DebugLog($"{GetPlayerName(playerId)} left the {tag} clan");

            RemoveBlueprints(playerId, learntBlueprints, ShareType.Clans);
        }

        private void OnClanDisbanded(List<string> memberIds)
        {
            if (!config.LoseBlueprintsOnLeave) return;

            int processedPlayers = 0;

            foreach (var memberId in memberIds)
            {
                var sharedBlueprints = GetSharedBlueprints(memberId, ShareType.Clans);
                if (sharedBlueprints == null || sharedBlueprints.Count == 0)
                {
                    continue;
                }

                if (ulong.TryParse(memberId, out var playerId))
                {
                    RemoveBlueprints(playerId, sharedBlueprints, ShareType.Clans);
                    processedPlayers++;
                }
                else
                {
                    // Fallback: if we somehow can't parse the player id, at least clear our tracking data
                    RemoveBlueprintsFromDatabase(ShareType.Clans, memberId, string.Empty);
                }
            }

            DebugLog($"Removed clan blueprints for {processedPlayers} player(s) on clan disband");
        }

        #endregion

        #region Core

        private bool TryShareBlueprint(ItemDefinition item, BasePlayer player)
        {
            if (item == null || player == null) return false;

            if (!permission.UserHasPermission(player.UserIDString, usePermission)) return false;

            if (BlueprintBlocked(item))
            {
                SendMessage("BlueprintBlocked", player, true, item.displayName.translated);

                return false;
            }

            var playerId = player.userID;

            if (SharingEnabled(player.UserIDString) && HasSocialConnections(playerId) && SomeoneWillLearnBlueprint(playerId, item))
            {
                var targetIds = GetCachedShareTargets(playerId);
                
                ShareBlueprint(player, targetIds, item);

                return true;
            }

            return false;
        }

        private static class ListPool<T>
        {
            private static readonly Stack<List<T>> pool = new Stack<List<T>>();

            public static List<T> Get()
            {
                return pool.Count > 0 ? pool.Pop() : new List<T>();
            }

            public static void Free(List<T> list)
            {
                list.Clear();
                pool.Push(list);
            }
        }

        private class UnlockTask
        {
            public ulong TargetId;
            public List<int> Blueprints = ListPool<int>.Get();
        }

        private bool QueueBlueprintUnlock(ulong playerId, ulong sharerId, int blueprintId, List<int> unlockQueue)
        {
            var playerInfo = ServerMgr.Instance.persistance.GetPlayerInfo(playerId);

            if (playerInfo == null) return false;

            var unlockedItems = playerInfo.unlockedItems;

            if (unlockedItems == null) return false;
            if (unlockedItems.Contains(blueprintId)) return false;

            unlockQueue.Add(blueprintId);

            DebugLog($"Added {GetItemNameById(blueprintId)} to unlock queue for {GetPlayerName(playerId)}");

            if (config.LoseBlueprintsOnLeave)
            {
                AddBlueprintToDatabase(playerId, sharerId, blueprintId);
            }

            return true;
        }

        private int ProcessQueuedBlueprintUnlocks(ulong playerId, List<int> unlockQueue)
        {
            if (unlockQueue.Count == 0) return 0;

            var persistance = ServerMgr.Instance.persistance;

            if (persistance == null) return 0;

            var playerInfo = persistance.GetPlayerInfo(playerId);

            if (playerInfo == null) return 0;

            playerInfo.unlockedItems.AddRange(unlockQueue);

            persistance.SetPlayerInfo(playerId, playerInfo);

            var player = RustCore.FindPlayerById(playerId);

            if (player != null)
            {
                foreach (var blueprint in unlockQueue)
                {
                    if (!player.PersistantPlayerInfo.unlockedItems.Contains(blueprint))
                    {
                        player.PersistantPlayerInfo.unlockedItems.Add(blueprint);
                    }

                    player.ClientRPC(RpcTarget.Player("UnlockedBlueprint", player), blueprint);
                }

                player.stats.Add("blueprint_studied", unlockQueue.Count);
                player.SendNetworkUpdateImmediate();

                PlaySoundEffect(player);
            }

            DebugLog($"Unlocked {unlockQueue.Count} blueprint(s) for {GetPlayerName(playerId)}");

            return unlockQueue.Count;
        }

        private void ShareBlueprint(BasePlayer sharer, List<ulong> targetIds, ItemDefinition item)
        {
            var blueprintId = item.itemid;
            var sharedCount = 0;
            var tasks = new List<UnlockTask>();

            DebugLog($"{sharer.displayName} is Sharing {GetItemNameById(blueprintId)} with {targetIds.Count()} player(s)");

            foreach (var targetId in targetIds)
            {
                if (targetId == sharer.userID) continue;

                var task = new UnlockTask
                {
                    TargetId = targetId
                };

                foreach (var blueprint in item.Blueprint.additionalUnlocks)
                {
                    QueueBlueprintUnlock(targetId, sharer.userID, blueprint.itemid, task.Blueprints);
                }

                QueueBlueprintUnlock(targetId, sharer.userID, blueprintId, task.Blueprints);

                sharedCount++;

                if (task.Blueprints.Count > 0)
                {
                    tasks.Add(task);
                }
                else
                {
                    ListPool<int>.Free(task.Blueprints);
                }
            }

            foreach (var task in tasks)
            {
                ProcessQueuedBlueprintUnlocks(task.TargetId, task.Blueprints);

                var target = RustCore.FindPlayerById(task.TargetId);

                if (target != null && config.ReceiveMessagesEnabled)
                {
                    SendMessage("TargetLearntBlueprint", target, true, sharer.displayName, item.displayName.translated);
                }

                ListPool<int>.Free(task.Blueprints);
            }

            if (sharedCount > 0)
            {
                if (config.ShareMessagesEnabled)
                {
                    SendMessage("PlayerSharedBlueprint", sharer, true, item.displayName.translated, sharedCount);
                }

                if (config.LoseBlueprintsOnLeave)
                {
                    MarkDataDirty();
                }
            }
        }

        private void ShareWithPlayer(BasePlayer sharer, BasePlayer target)
        {
            if (sharer == null || target == null) return;

            DebugLog($"{sharer.displayName} started sharing their blueprints with {target.displayName}");

            var playerId = sharer.userID;
            var targetId = target.userID;

            if (!SharingEnabled(target.UserIDString))
            {
                SendMessage("TargetSharingDisabled", sharer, true, target.displayName);

                return;
            }

            if (!SameTeam(playerId, targetId) && !SameClan(playerId, targetId) && !AreMutualFriends(playerId, targetId) && !permission.UserHasPermission(sharer.UserIDString, bypassPermission))
            {
                SendMessage("CannotShare", sharer, true);

                return;
            }

            var filteredBlueprints = RemoveBlockedBlueprints(sharer.PersistantPlayerInfo.unlockedItems);

            if (filteredBlueprints.Count == 0)
            {
                SendMessage("NoBlueprintsToShare", sharer, true, target.displayName);

                return;
            }

            var queue = ListPool<int>.Get();

            foreach (var blueprintId in filteredBlueprints)
            {
                QueueBlueprintUnlock(targetId, playerId, blueprintId, queue);
            }

            var unlocked = ProcessQueuedBlueprintUnlocks(targetId, queue);

            ListPool<int>.Free(queue);

            if (unlocked > 0)
            {
                if (config.LoseBlueprintsOnLeave)
                {
                    MarkDataDirty();
                }

                if (config.ShareMessagesEnabled)
                {
                    SendMessage("PlayerSharedBlueprints", sharer, true, unlocked, target.displayName);
                }

                if (config.ReceiveMessagesEnabled)
                {
                    SendMessage("TargetLearntBlueprints", target, true, sharer.displayName, unlocked);
                }
            }
            else
            {
                SendMessage("NoBlueprintsToShare", sharer, true, target.displayName);
            }
        }

        private bool HasSocialConnections(ulong playerId)
        {
            return InTeam(playerId) || InClan(playerId) || HasFriends(playerId);
        }

        private List<int> RemoveBlockedBlueprints(List<int> blueprints)
        {
            return blueprints
                .Select(ItemManager.FindItemDefinition)
                .Where(item => !BlueprintBlocked(item))
                .Select(item => item.itemid)
                .ToList();
        }

        private bool SomeoneWillLearnBlueprint(ulong playerId, ItemDefinition item)
        {
            if (item == null) return false;

            var targetIds = GetCachedShareTargets(playerId);

            if (targetIds.Count == 0) return false;

            var willLearn = false;

            foreach (var targetId in targetIds)
            {
                if (PlayerWouldLearnBlueprint(targetId, item.itemid))
                {
                    willLearn = true;
                }

                foreach (var blueprintItem in item.Blueprint.additionalUnlocks)
                {
                    if (PlayerWouldLearnBlueprint(targetId, blueprintItem.itemid))
                    {
                        willLearn = true;
                    }
                }
            }

            return willLearn;
        }

        private bool PlayerWouldLearnBlueprint(ulong playerId, int blueprintId)
        {
            var playerInfo = ServerMgr.Instance.persistance.GetPlayerInfo(playerId);

            if (playerInfo == null) return false;

            var unlockedItems = playerInfo.unlockedItems;

            if (unlockedItems.Contains(blueprintId)) return false;

            return true;
        }

        private List<ulong> GetPlayerIdsToShareWith(ulong playerId) 
        {
            var ids = new HashSet<ulong>();

            if (isClansEnabled && InClan(playerId))
            {
                var clanMembers = GetClanMembers(playerId);

                if (clanMembers != null)
                {
                    ids.UnionWith(clanMembers);
                }
            }

            if (isFriendsEnabled && HasFriends(playerId))
            {
                var mutualFriends = GetFriends(playerId).Where(friendId => AreMutualFriends(playerId, friendId));

                ids.UnionWith(mutualFriends);
            }

            if (isTeamsEnabled && InTeam(playerId))
            {
                var teamMembers = GetTeamMembers(playerId);

                if (teamMembers != null)
                {
                    ids.UnionWith(teamMembers);
                }
            }

            ids.Remove(playerId);

            return ids.ToList();
        }

        private List<ulong> GetCachedShareTargets(ulong playerId)
        {
            if (shareTargetCache == null)
            {
                shareTargetCache = new Dictionary<ulong, List<ulong>>();
            }

            if (shareTargetCache.TryGetValue(playerId, out var cached))
            {
                return cached;
            }

            var targetIds = GetPlayerIdsToShareWith(playerId);

            if (targetIds == null)
            {
                targetIds = Enumerable.Empty<ulong>().ToList();
            }

            var freshTargets = new List<ulong>();

            foreach (var targetId in targetIds)
            {
                if (SharingEnabled(targetId.ToString()))
                {
                    freshTargets.Add(targetId);
                }
            }

            shareTargetCache[playerId] = freshTargets;

            if (shareTargetCacheTimers.TryGetValue(playerId, out var oldTimer))
            {
                oldTimer.Destroy();
                shareTargetCacheTimers.Remove(playerId);
            }

            shareTargetCacheTimers[playerId] = timer.Once(shareTargetCacheDuration, () =>
            {
                shareTargetCache.Remove(playerId);
                shareTargetCacheTimers.Remove(playerId);
            });

            return freshTargets;
        }

        private void AddBlueprintToDatabase(ulong playerId, ulong sharerId, int blueprint)
        {
            var playerIdStr = playerId.ToString();
            var sharerIdStr = sharerId.ToString();

            if (isTeamsEnabled && SameTeam(playerId, sharerId))
            {
                var list = GetSharedBlueprints(playerIdStr, ShareType.Teams);

                if (!list.Contains(blueprint))
                {
                    list.Add(blueprint);

                    DebugLog($"Added {GetItemNameById(blueprint)} to team blueprints for {GetPlayerName(playerId)}");
                }
            }

            if (isClansEnabled && SameClan(playerId, sharerId))
            {
                var list = GetSharedBlueprints(playerIdStr, ShareType.Clans);

                if (!list.Contains(blueprint))
                {
                    list.Add(blueprint);

                    DebugLog($"Added {GetItemNameById(blueprint)} to clan blueprints for {GetPlayerName(playerId)}");
                }
            }

            if (isFriendsEnabled && IsFriend(sharerId, playerId))
            {
                var list = GetSharedBlueprints(playerIdStr, ShareType.Friends, sharerIdStr);

                if (!list.Contains(blueprint))
                {
                    list.Add(blueprint);

                    DebugLog($"Added {GetItemNameById(blueprint)} to friend {GetPlayerName(sharerId)} blueprints for {GetPlayerName(playerId)}");
                }
            }
        }

        private void RemoveBlueprintsFromDatabase(ShareType type, string playerId, string friendId)
        {
            var list = GetSharedBlueprints(playerId, type, friendId);

            list.Clear();

            MarkDataDirty();

            DebugLog($"Removed {type} blueprints for {GetPlayerName(ulong.Parse(playerId))} from database");
        }

        private void RemoveBlueprints(ulong playerId, HashSet<int> blueprintIds, ShareType type, string friendId = "")
        {
            if (blueprintIds == null || blueprintIds.Count == 0) return;

            var playerInfo = ServerMgr.Instance.persistance.GetPlayerInfo(playerId);
            
            if (playerInfo == null) return;

            var player = RustCore.FindPlayerById(playerId);
            int blueprintsRemoved = 0;

            foreach (var blueprintId in blueprintIds)
            {
                if (!playerInfo.unlockedItems.Contains(blueprintId)) continue;

                playerInfo.unlockedItems.Remove(blueprintId);
                blueprintsRemoved++;

                DebugLog($"Removed blueprint {GetItemNameById(blueprintId)} from {GetPlayerName(playerId)}");

                // If player is online, remove from their in-memory list too
                if (player != null && player.PersistantPlayerInfo.unlockedItems.Contains(blueprintId))
                {
                    player.PersistantPlayerInfo.unlockedItems.Remove(blueprintId);
                }
            }

            if (blueprintsRemoved == 0) return;

            ServerMgr.Instance.persistance.SetPlayerInfo(playerId, playerInfo);

            RemoveBlueprintsFromDatabase(type, playerId.ToString(), friendId);

            if (player != null)
            {
                player.SendNetworkUpdateImmediate();
                SendMessage("BlueprintsRemoved", player, true, blueprintsRemoved);
            }
        }

        private void PlayerLeftTeam(ulong playerId)
        {
            if (!config.LoseBlueprintsOnLeave) return;

            var sharedBlueprints = GetSharedBlueprints(playerId.ToString(), ShareType.Teams);

            if (sharedBlueprints.Count == 0) return;

            RemoveBlueprints(playerId, sharedBlueprints, ShareType.Teams);
        }

        #endregion

        #region Utility

        private void PlaySoundEffect(BasePlayer player)
        {
            if (player == null) return;

            var soundEffect = new Effect("assets/prefabs/deployable/research table/effects/research-success.prefab", player.transform.position, Vector3.zero);

            if (soundEffect == null) return;

            EffectNetwork.Send(soundEffect, player.net.connection);
        }

        private List<BasePlayer> FindPlayersByIds(IEnumerable<ulong> ids, ulong excludeId = 0)
        {
            return ids
                .Where(id => id != excludeId)
                .Select(RustCore.FindPlayerById)
                .Where(player => player != null)
                .Distinct()
                .ToList();
        }

        private bool TryGetOtherPlayer(BasePlayer sender, string name, out BasePlayer target)
        {
            target = RustCore.FindPlayerByName(name);

            if (target == null)
            {
                SendMessage("PlayerNotFound", sender, true);

                return false;
            }

            if (target == sender)
            {
                SendMessage("TargetEqualsPlayer", sender, true);

                return false;
            }

            return true;
        }

        private bool BlueprintBlocked(ItemDefinition item) => item == null || config.BlockedItems.Contains(item.shortname);

        private Dictionary<int, List<string>> GroupBlueprintsByWorkbenchLevel(HashSet<int> sharedBlueprints)
        {
            var groupedBlueprints = new Dictionary<int, List<string>>();

            foreach (var item in sharedBlueprints.Select(ItemManager.FindItemDefinition).Where(i => i != null))
            {
                var tier = item.Blueprint.workbenchLevelRequired;

                if (!groupedBlueprints.TryGetValue(tier, out var list))
                {
                    list = Pool.Get<List<string>>();

                    groupedBlueprints[tier] = list;
                }

                list.Add(item.displayName.translated);
            }

            return groupedBlueprints;
        }

        private void DisplayLearntBlueprints(BasePlayer player, ShareType type, string friendId = "")
        {
            if (player == null) return;

            var sharedBlueprints = GetSharedBlueprints(player.UserIDString, type, friendId);

            if (sharedBlueprints == null || sharedBlueprints.Count == 0)
            {
                SendMessage("NoSharedBlueprints", player, true);

                return;
            }

            var groupedBlueprints = GroupBlueprintsByWorkbenchLevel(sharedBlueprints);

            SendMessage("SharedBlueprintsTitle", player, true);

            foreach (var kvp in groupedBlueprints.OrderBy(k => k.Key))
            {
                var tier = kvp.Key;
                var names = kvp.Value;

                var msg = GetLangValue($"ShowSharedBlueprints", player.UserIDString, tier.ToString(), string.Join(", ", names));

                player.ChatMessage(msg);                
            }

            FreeGroupedBlueprints(groupedBlueprints);
        }

        private void FreeGroupedBlueprints(Dictionary<int, List<string>> grouped)
        {
            foreach (var key in grouped.Keys.ToList())
            {
                var list = grouped[key];

                Pool.FreeUnmanaged(ref list);

                grouped[key] = null;
            }
        }

        private void DebugLog(string message, DebugLevel level = DebugLevel.Info)
        {
            if (!config.Debug) return;

            string prefixColor = level switch
            {
                DebugLevel.Warning => "#f1c40f",// yellow
                DebugLevel.Error => "#e74c3c",// red
                _ => "#3498db",// blue
            };

            PrintToConsole($"<color={prefixColor}>[BlueprintShare Debug]</color> {message}");
        }

        private string GetItemNameById(int itemId)
        {
            return ItemManager.FindItemDefinition(itemId)?.displayName?.translated ?? string.Empty;
        }

        private string GetPlayerName(ulong playerId)
        {
            return RustCore.FindPlayerById(playerId)?.displayName
                ?? covalence.Players.FindPlayerById(playerId.ToString())?.Name
                ?? "Unknown";
        }

        #endregion

        #region Chat Commands

        [ChatCommand("bs")]
        private void ChatCommands(BasePlayer player, string command, string[] args)
        {
            if (args.Length == 0)
            {
                HelpCommand(player, args);

                return;
            }

            var key = args[0].ToLower();

            if (chatCommandHandlers.TryGetValue(key, out var handler))
            {
                handler(player, args);
            }
            else
            {
                SendMessage("ArgumentsError", player, true);
            }
        }

        private void ToggleCommand(BasePlayer player, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, togglePermission))
            {
                SendMessage("NoPermission", player, true);

                return;
            }

            var data = GetPlayerData(player.UserIDString);

            var oldState = data.SharingEnabled;

            data.SharingEnabled = !oldState;

            MarkDataDirty();

            SendMessage(oldState ? "ToggleOff" : "ToggleOn", player, true);
        }

        private void ShareCommand(BasePlayer player, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, sharePermission))
            {
                SendMessage("NoPermission", player, true);

                return;
            }

            if (args.Length != 2)
            {
                SendMessage("NoTarget", player, true);

                return;
            }

            if (!TryGetOtherPlayer(player, args[1], out var target)) return;

            ShareWithPlayer(player, target);
        }

        private void ShowCommand(BasePlayer player, string[] args)
        {
            if (!config.LoseBlueprintsOnLeave)
            {
                SendMessage("LoseBlueprintsDisabled", player, true);

                return;
            }

            if (!permission.UserHasPermission(player.UserIDString, showPermission))
            {
                SendMessage("NoPermission", player, true);

                return;
            }

            if (args.Length < 2)
            {
                SendMessage("ShowMissingArgument", player, true);

                return;
            }

            switch (args[1])
            {
                case "clan":
                    {
                        DisplayLearntBlueprints(player, ShareType.Clans);

                        break;
                    }

                case "team":
                    {
                        DisplayLearntBlueprints(player, ShareType.Teams);

                        break;
                    }

                case "friend":
                    {
                        if (args.Length != 3)
                        {
                            SendMessage("ShowFriendArgumentMissing", player, true);

                            return;
                        }

                        if (!TryGetOtherPlayer(player, args[2], out var friend)) return;

                        if (!IsFriend(friend.userID, player.userID))
                        {
                            SendMessage("NotFriends", player, true);

                            return;
                        }

                        DisplayLearntBlueprints(player, ShareType.Friends, friend.UserIDString);

                        break;
                    }

                default:
                    {
                        SendMessage("ShowMissingArgument", player, true);

                        return;
                    }
            }
        }

        private void HelpCommand(BasePlayer player, string[] args)
        {
            SendMessage("Help", player, false);
        }

        #endregion

        #region Configuration File

        private Configuration config;

        private class Configuration
        {
            [JsonProperty("Teams Sharing Enabled")]
            public bool TeamsEnabled = true;

            [JsonProperty("Clans Sharing Enabled")]
            public bool ClansEnabled = true;

            [JsonProperty("Friends Sharing Enabled")]
            public bool FriendsEnabled = true;

            [JsonProperty("Share Blueprint Items")]
            public bool ItemSharingEnabled = true;

            [JsonProperty("Share Tech Tree Blueprints")]
            public bool TechTreeSharingEnabled = true;

            [JsonProperty("Share Blueprints To Existing Members")]
            public bool ShareToExistingMembers;

            [JsonProperty("Share Blueprints To New Members")]
            public bool ShareToNewMembers;

            [JsonProperty("Lose Blueprints on Leave")]
            public bool LoseBlueprintsOnLeave;

            [JsonProperty("Clear Data File on Wipe")]
            public bool ClearDataOnWipe = true;

            [JsonProperty("Receive Messages Enabled")]
            public bool ReceiveMessagesEnabled = true;

            [JsonProperty("Share Messages Enabled")]
            public bool ShareMessagesEnabled = true;

            [JsonProperty("Message Icon")]
            public string MessageIcon = "0";

            [JsonProperty("Items Blocked from Sharing")]
            public HashSet<string> BlockedItems = new HashSet<string>();

            [JsonProperty("Debug Mode")]
            public bool Debug = false;
        }

        protected override void LoadDefaultConfig()
        {
            PrintWarning("Generating new configuration file");

            config = new Configuration();
        }

        protected override void SaveConfig()
        {
            PrintWarning("Configuration file has been saved");

            Config.WriteObject(config, true);
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();

            try
            {
                config = Config.ReadObject<Configuration>();

                if (config == null)
                {
                    throw new Exception();
                }
            }
            catch
            {
                PrintError($"An error occurred while parsing the configuration file {Name}.json; Resetting configuration to default values.");
                LoadDefaultConfig();
            }
        }

        #endregion

        #region Data File

        private class StoredData
        {
            public Dictionary<string, PlayerData> Players = new Dictionary<string, PlayerData>();
        }

        private class PlayerData
        {
            public bool SharingEnabled;

            public ShareData LearntBlueprints;
        }

        private class ShareData
        {
            [JsonIgnore] public HashSet<int> Team = new HashSet<int>();
            [JsonIgnore] public HashSet<int> Clan = new HashSet<int>();
            [JsonIgnore] public Dictionary<string, HashSet<int>> Friends = new Dictionary<string, HashSet<int>>();

            [JsonProperty("Team")]
            public List<int> TeamList
            {
                get => Team?.ToList() ?? new List<int>();
                set => Team = value != null ? new HashSet<int>(value) : new HashSet<int>();
            }

            [JsonProperty("Clan")]
            public List<int> ClanList
            {
                get => Clan?.ToList() ?? new List<int>();
                set => Clan = value != null ? new HashSet<int>(value) : new HashSet<int>();
            }

            [JsonProperty("Friends")]
            public Dictionary<string, List<int>> FriendsList
            {
                get => Friends?.ToDictionary(kv => kv.Key, kv => kv.Value.ToList()) ?? new Dictionary<string, List<int>>();
                set => Friends = value != null
                    ? value.ToDictionary(kv => kv.Key, kv => new HashSet<int>(kv.Value))
                    : new Dictionary<string, HashSet<int>>();
            }

            public void InitializeAfterLoad()
            {
                Team = new HashSet<int>(TeamList ?? new List<int>());
                Clan = new HashSet<int>(ClanList ?? new List<int>());
                Friends = FriendsList != null
                    ? FriendsList.ToDictionary(
                        kv => kv.Key,
                        kv => new HashSet<int>(kv.Value ?? new List<int>())
                    )
                    : new Dictionary<string, HashSet<int>>();
            }
        }

        private void CreateData()
        {
            storedData = new StoredData();

            SaveData();

            DebugLog("Data file has been created");
        }

        private void LoadData()
        {
            if (Interface.Oxide.DataFileSystem.ExistsDatafile(Name))
            {
                storedData = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(Name) ?? new StoredData();

                foreach (var playerData in storedData.Players.Values)
                {
                    playerData.LearntBlueprints.InitializeAfterLoad();
                }

                DebugLog($"Loaded data for {storedData.Players.Count} player(s)");
            }
            else
            {
                CreateData();
            }
        }

        private void SaveData()
        {
            Interface.Oxide.DataFileSystem.WriteObject(Name, storedData);

            dataDirty = false;

            DebugLog($"Data file has been saved");
        }

        private void MarkDataDirty() => dataDirty = true;

        private PlayerData GetPlayerData(string playerId)
        {
            if (storedData == null)
            {
                CreateData();
            }

            if (!storedData.Players.TryGetValue(playerId, out var data))
            {
                data = new PlayerData
                {
                    SharingEnabled = true,
                    LearntBlueprints = new ShareData()
                };

                storedData.Players[playerId] = data;
            }

            return data;
        }

        private HashSet<int> GetSharedBlueprints(string playerId, ShareType type, string friendId = "")
        {
            var data = GetPlayerData(playerId).LearntBlueprints;

            DebugLog($"Getting {type} blueprints for {GetPlayerName(ulong.Parse(playerId))}");

            return type switch
            {
                ShareType.Teams => data.Team,
                ShareType.Clans => data.Clan,
                ShareType.Friends => data.Friends.GetOrCreate(friendId),
                _ => throw new ArgumentOutOfRangeException(nameof(type))
            };
        }

        #endregion

        #region Localization File

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["Prefix"] = "<color=#D85540>[Blueprint Share]</color> ",
                ["ArgumentsError"] = "Incorrect command usage. Try <color=#ffff00>/bs help</color>.",
                ["Help"] = "<color=#D85540>Blueprint Share Commands:</color>\n" +
                           "<color=#D85540>/bs toggle</color> - Toggle automatic blueprint sharing\n" +
                           "<color=#D85540>/bs share <player></color> - Share your learned blueprints with a player\n" +
                           "<color=#D85540>/bs show <team|clan|friend> [name]</color> - View blueprints shared with you\n" +
                           "<color=#D85540>/bs help</color> - Show this help menu",
                ["ToggleOn"] = "Blueprint sharing is now <color=#00ff00>enabled</color>.",
                ["ToggleOff"] = "Blueprint sharing is now <color=#ff0000>disabled</color>.",
                ["NoPermission"] = "You don't have permission to use this command.",
                ["CannotShare"] = "You cannot share blueprints with this player, they must be in your team, clan, or friends list.",
                ["NoTarget"] = "You must specify a player to share with.",
                ["TargetEqualsPlayer"] = "You cannot share blueprints with yourself.",
                ["PlayerNotFound"] = "Player not found.",
                ["PlayerSharedBlueprints"] = "You shared <color=#ffff00>{0}</color> blueprint(s) with <color=#ffff00>{1}</color>.",
                ["TargetLearntBlueprints"] = "<color=#ffff00>{0}</color> has shared <color=#ffff00>{1}</color> blueprint(s) with you.",
                ["NoBlueprintsToShare"] = "You have no new blueprints to share with <color=#ffff00>{0}</color>.",
                ["PlayerSharedBlueprint"] = "You have learned the <color=#ffff00>{0}</color> blueprint and shared it with <color=#ffff00>{1}</color> player(s).",
                ["TargetLearntBlueprint"] = "<color=#ffff00>{0}</color> has shared the <color=#ffff00>{1}</color> blueprint with you.",
                ["BlueprintBlocked"] = "The <color=#ffff00>{0}</color> blueprint is blocked from sharing, but you have still learned it.",
                ["TargetSharingDisabled"] = "Cannot share blueprints with <color=#ffff00>{0}</color> — they have disabled sharing.",
                ["BlueprintsRemoved"] = "You have lost access to <color=#ffff00>{0}</color> blueprint(s).",
                ["ShowMissingArgument"] = "You must specify which shared blueprints to view. Options: <color=#ffff00>team</color>, <color=#ffff00>clan</color>, or <color=#ffff00>friend</color>.",
                ["ShowFriendArgumentMissing"] = "You must specify a friend's name.",
                ["NotFriends"] = "You are not friends with this player.",
                ["NoSharedBlueprints"] = "No blueprints have been shared with you.",
                ["SharedBlueprintsTitle"] = "Blueprints Shared With You:",
                ["ShowSharedBlueprints"] = "<color=#ffff00>Tier {0}:</color> {1}",
                ["LoseBlueprintsDisabled"] = "This feature has been disabled by the server administrator."
            }, this);
        }

        private string GetLangValue(string key, string id = null, params object[] args)
        {
            var msg = lang.GetMessage(key, this, id);

            return string.IsNullOrWhiteSpace(msg) ? $"[{key}]" : (args.Length > 0 ? string.Format(msg, args) : msg);
        }

        private void SendMessage(string key, BasePlayer player, bool prefix, params object[] args)
        {
            if (player == null || string.IsNullOrEmpty(key)) return;

            var sb = new StringBuilder(GetLangValue(key, player.UserIDString, args));

            if (prefix)
            {
                sb.Insert(0, GetLangValue("Prefix", player.UserIDString, args));
            }

            var chatIcon = 0ul;

            if (!string.IsNullOrEmpty(config.MessageIcon) && !ulong.TryParse(config.MessageIcon, out chatIcon))
            {
                PrintWarning($"Invalid MessageIcon '{config.MessageIcon}' — defaulting to 0.");
                chatIcon = 0;
            }

            Player.Reply(player, sb.ToString(), chatIcon);
        }

        #endregion

        #region API

        private bool SharingEnabled(string playerId)
        {
            var data = GetPlayerData(playerId);

            return data?.SharingEnabled ?? true;
        }

        #endregion
    }

    static class DictionaryExtensions
    {
        public static TValue GetOrCreate<TKey, TValue>(this Dictionary<TKey, TValue> dict, TKey key) where TValue : new()
        {
            if (!dict.TryGetValue(key, out var value))
            {
                value = new TValue();
                dict[key] = value;
            }

            return value;
        }
    }
}