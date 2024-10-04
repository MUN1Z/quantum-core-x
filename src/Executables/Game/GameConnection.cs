﻿using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using QuantumCore.API;
using QuantumCore.API.Game.Types;
using QuantumCore.API.Game.World;
using QuantumCore.Caching;
using QuantumCore.Core.Networking;
using QuantumCore.Networking;

namespace QuantumCore.Game
{
    public class GameConnection : Connection, IGameConnection
    {
        private readonly IWorld _world;
        private readonly ILogger<GameConnection> _logger;
        private readonly ICacheManager _cacheManager;
        public IServerBase Server { get; }
        public Guid? AccountId { get; set; }
        public string Username { get; set; } = "";
        public IPlayerEntity? Player { get; set; }
        

        public GameConnection(IServerBase server, TcpClient client, ILogger<GameConnection> logger,
            PluginExecutor pluginExecutor, IWorld world, IPacketReader packetReader, ICacheManager cacheManager)
            : base(logger, pluginExecutor, packetReader)
        {
            _world = world;
            _logger = logger;
            Server = server;
            _cacheManager = cacheManager;
            Init(client);
        }

        protected override void OnHandshakeFinished()
        {
            GameServer.Instance.CallConnectionListener(this);
        }

        protected override async Task OnClose(bool expected = true)
        {
            if (Player != null)
            {
                if (expected)
                {
                    _world.DespawnEntity(Player);
                }
                else
                {
                    if (Phase is EPhases.Game or EPhases.Loading)
                    {
                        await Player.CalculatePlayedTimeAsync();
                    }

                    // In case of unexpected disconnection, we need to save the player's state
                    if (Phase is EPhases.Game or EPhases.Loading or EPhases.Select)
                    {
                        await _world.DespawnPlayerAsync(Player);
                        
                    }
                }
                
                _cacheManager.Shared.DelAllAsync($"*{AccountId}");
                _cacheManager.Server.DelAllAsync($"player:{Player!.Player.Id}");
                
            }

            await Server.RemoveConnection(this);

            // todo enable expiry on auth token
        }

        protected override async Task OnReceive(IPacketSerializable packet)
        {
            await Server.CallListener(this, packet);
        }

        protected override long GetServerTime()
        {
            return Server.ServerTime;
        }
    }
}
