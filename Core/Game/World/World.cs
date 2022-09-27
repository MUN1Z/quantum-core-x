﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BeetleX.Redis;
using Microsoft.Extensions.Logging;
using QuantumCore.API;
using QuantumCore.API.Core.Models;
using QuantumCore.API.Game.World;
using QuantumCore.Core.Cache;
// using QuantumCore.Core.API;
using QuantumCore.Core.Utils;
using QuantumCore.Game.Quest;
using QuantumCore.Game.World.Entities;
using Serilog;
using Tomlyn;
using Tomlyn.Model;

namespace QuantumCore.Game.World
{
    public class World : IWorld
    {
        private readonly ILogger<World> _logger;
        private readonly PluginExecutor _pluginExecutor;
        private uint _vid;
        private readonly Grid<IMap> _world = new(0, 0);
        private readonly Dictionary<string, IMap> _maps = new();
        private readonly Dictionary<string, IPlayerEntity> _players = new();
        private readonly Dictionary<int, SpawnGroup> _groups = new();

        private readonly Dictionary<int, Shop> _staticShops = new();

        private Subscriber _mapSubscriber;
        private readonly IItemManager _itemManager;
        private readonly IMonsterManager _monsterManager;
        private readonly IAnimationManager _animationManager;
        private readonly ICacheManager _cacheManager;

        public static IWorld Instance { get; set; }
        
        public World(ILogger<World> logger, PluginExecutor pluginExecutor, IItemManager itemManager, 
            IMonsterManager monsterManager, IAnimationManager animationManager, ICacheManager cacheManager)
        {
            _logger = logger;
            _pluginExecutor = pluginExecutor;
            _itemManager = itemManager;
            _monsterManager = monsterManager;
            _animationManager = animationManager;
            _cacheManager = cacheManager;
            _vid = 0;
            Instance = this;
        }
        
        public async Task Load()
        {
            LoadShops();
            LoadGroups();
            LoadAtlasInfo();
            await LoadRemoteMaps();

            // Initialize maps, spawn monsters etc
            foreach (var map in _maps.Values)
            {
                if (map is Map m)
                {
                    await m.Initialize();
                }
            }
        }

        private void LoadShops()
        {
            var path = Path.Join("data", "shops.toml");
            if (File.Exists(path))
            {
                var toml = Toml.Parse(File.ReadAllText(path));
                var model = toml.ToModel();

                if (model["shop"] is not TomlTableArray shops)
                {
                    Log.Warning("Failed to read shops.toml");
                    return;
                }
                
                foreach (var shopDef in shops)
                {
                    var id = (int)(long) shopDef["id"];
                    var shop = new Shop (_itemManager){Name = (string) shopDef["name"]};

                    if (shopDef.ContainsKey("items"))
                    {
                        if (shopDef["items"] is not TomlTableArray items)
                        {
                            Log.Warning($"Can't read items of shop {shop.Name}");
                            return;
                        }

                        foreach (var itemDef in items)
                        {
                            var itemId = (uint) (long) itemDef["id"];
                            byte count = 1;
                            if (itemDef.ContainsKey("count"))
                            {
                                count = (byte) (long) itemDef["count"];
                            }

                            var price = 0u;
                            if (itemDef.ContainsKey("price"))
                            {
                                price = (uint) (long) itemDef["price"];
                            }

                            shop.AddItem(itemId, count, price);
                        }
                    }
                    
                    _staticShops[id] = shop;
                    
                    if (shopDef.ContainsKey("npc"))
                    {
                        var npc = (uint) (long) shopDef["npc"];
                        GameEventManager.RegisterNpcClickEvent(shop.Name, npc, async player =>
                        {
                            await shop.Open(player);
                        });
                    }
                }
            }
        }

        private void LoadGroups()
        {
            // Load groups
            var path = Path.Join("data", "groups.toml");
            if (File.Exists(path))
            {
                var toml = Toml.Parse(File.ReadAllText(path));
                var model = toml.ToModel();
                if (model["group"] is TomlTableArray groups)
                {
                    foreach (var group in groups)
                    {
                        var g = WorldUtils.GroupFromToml(group);
                        _groups[g.Id] = g;
                    }
                }
            }
        }

        private void LoadAtlasInfo()
        {
            // Regex for parsing lines in the atlas info
            var regex = new Regex(@"^([a-zA-Z0-9\/_]+)[\s]+([0-9]+)[\s]+([0-9]+)[\s]+([0-9]+)[\s]+([0-9]+)$");
                
            var maxX = 0u;
            var maxY = 0u;
                
            // Load atlasinfo.txt and initialize all maps the game core hosts
            if (!File.Exists("data/atlasinfo.txt"))
            {
                 throw new FileNotFoundException("Unable to find file data/atlasinfo.txt");
            }

            using var reader = new StreamReader("data/atlasinfo.txt");
            string line;
            var lineNo = 0;
            while ((line = reader.ReadLine()) != null)
            {
                lineNo++;
                if(string.IsNullOrWhiteSpace(line)) continue; // skip empty lines

                var match = regex.Match(line);
                if (match.Success)
                {
                    try
                    {
                        var mapName = match.Groups[1].Value;
                        var positionX = uint.Parse(match.Groups[2].Value);
                        var positionY = uint.Parse(match.Groups[3].Value);
                        var width = uint.Parse(match.Groups[4].Value);
                        var height = uint.Parse(match.Groups[5].Value);

                        IMap map;
                        if (!ConfigManager.Maps.Contains(mapName))
                        {
                            map = new RemoteMap(mapName, positionX, positionY, width, height);
                        }
                        else
                        {
                            map = new Map(_monsterManager, _animationManager, _cacheManager, mapName, positionX, positionY, width, height);    
                        }
                        
                        
                        _maps[map.Name] = map;

                        if (positionX + width * Map.MapUnit > maxX) maxX = positionX + width * Map.MapUnit;
                        if (positionY + height * Map.MapUnit > maxY) maxY = positionY + height * Map.MapUnit;
                    }
                    catch (FormatException)
                    {
                        throw new InvalidDataException($"Failed to parse atlasinfo.txt:line {lineNo} - Failed to parse number");
                    }
                }
                else
                {
                    throw new InvalidDataException($"Failed to parse atlasinfo.txt:line {lineNo} - Failed to parse line");
                }
            }

            // Initialize world grid and place maps on it
            _world.Resize(maxX / Map.MapUnit, maxY / Map.MapUnit);
            foreach (var map in _maps.Values)
            {
                for (var x = map.UnitX; x < map.UnitX + map.Width; x++)
                {
                    for (var y = map.UnitY; y < map.UnitY + map.Height; y++)
                    {
                        _world.Set(x, y, map);
                    }
                }
            }
        }

        private async Task LoadRemoteMaps()
        {
            var keys = await _cacheManager.Keys("maps:*");

            foreach (var key in keys)
            {
                var mapName = key[5..];
                var map = _maps[mapName];
                if (map is not RemoteMap remoteMap)
                {
                    continue;
                }

                var address = await _cacheManager.Get<string>(key);
                var parts = address.Split(":");
                Debug.Assert(parts.Length == 2);
                    
                remoteMap.Host = IPAddress.Parse(parts[0]);
                remoteMap.Port = ushort.Parse(parts[1]);
                
                Log.Debug($"Map {remoteMap.Name} is available at {remoteMap.Host}:{remoteMap.Port}");
            }

            _mapSubscriber = _cacheManager.Subscribe();
            _mapSubscriber.Register<string>("maps", mapDetails =>
            {
                var data = mapDetails.Split(" ");
                Debug.Assert(data.Length == 2);

                var mapName = data[0];
                var parts = data[1].Split(":");
                Debug.Assert(parts.Length == 2);

                var map = _maps[mapName];
                if (map is not RemoteMap remoteMap)
                {
                    return;
                }
                
                remoteMap.Host = IPAddress.Parse(parts[0]);
                remoteMap.Port = ushort.Parse(parts[1]);
                
                Log.Debug($"Map {remoteMap.Name} is now available at {remoteMap.Host}:{remoteMap.Port}");
            });
            
            _mapSubscriber.Listen();
        }
        
        public void Update(double elapsedTime)
        {
            // HookManager.Instance.CallHook<IHookWorldUpdate>(elapsedTime);

            foreach (var map in _maps.Values)
            {
                map.Update(elapsedTime);
            }
        }

        public IMap GetMapAt(uint x, uint y)
        {
            var gridX = x / Map.MapUnit;
            var gridY = y / Map.MapUnit;

            return _world.Get(gridX, gridY);
        }

        public IMap GetMapByName(string name)
        {
            return _maps[name];
        }

        public List<IMap> FindMapsByName(string needle)
        {
            var list = new List<IMap>();
            foreach (var (name, map) in _maps)
            {
                if (name == needle)
                {
                    list.Clear();
                    list.Add(map);
                    return list;
                }
                
                if (name.Contains(needle, StringComparison.InvariantCultureIgnoreCase))
                {
                    list.Add(map);
                }
            }
            return list;
        }

        public CoreHost GetMapHost(int x, int y)
        {
            var map = GetMapAt((uint) x, (uint) y);
            if (map == null)
            {
                Log.Warning($"No available host for map at {x}x{y}");
                return new CoreHost {Ip = IPAddress.None, Port = 0};
            }

            if (map is RemoteMap remoteMap)
            {
                return new CoreHost {Ip = remoteMap.Host, Port = remoteMap.Port};
            }

            return new CoreHost {Ip = IpUtils.PublicIP, Port = (ushort) GameServer.Instance.Port};
        }

        public SpawnGroup GetGroup(int id)
        {
            if (!_groups.ContainsKey(id))
            {
                return null;
            }
            return _groups[id];
        }

        public async ValueTask<bool> SpawnEntity(IEntity e)
        {
            var map = GetMapAt((uint) e.PositionX, (uint) e.PositionY);
            if (map == null) return false;

            if (e.GetType() == typeof(PlayerEntity))
                AddPlayer((PlayerEntity)e);

            await _pluginExecutor.ExecutePlugins<IGameEntityLifetimeListener>(_logger, x => x.OnPreCreatedAsync());
            var result = map.SpawnEntity(e);
            await _pluginExecutor.ExecutePlugins<IGameEntityLifetimeListener>(_logger, x => x.OnPostCreatedAsync());
            return result;
        }

        public async Task DespawnEntity(IEntity entity)
        {
            if (entity is PlayerEntity player)
            {
                RemovePlayer(player);
            }
            
            await _pluginExecutor.ExecutePlugins<IGameEntityLifetimeListener>(_logger, x => x.OnPreDeletedAsync());
            entity.Map?.DespawnEntity(entity);
            await _pluginExecutor.ExecutePlugins<IGameEntityLifetimeListener>(_logger, x => x.OnPostDeletedAsync());
        }

        public uint GenerateVid()
        {
            return ++_vid;
        }

        private void AddPlayer(IPlayerEntity e)
        {
            if (_players.ContainsKey(e.Name))
                _players[e.Name] = e;
            else
                _players.Add(e.Name, e);
        }

        public void RemovePlayer(IPlayerEntity e)
        {
            _players.Remove(e.Name);
        }

        public IPlayerEntity GetPlayer(string playerName)
        {
            return _players.ContainsKey(playerName) ? _players[playerName] : null;
        }
    }
}
