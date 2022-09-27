﻿using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using QuantumCore.API;
using QuantumCore.API.Game.Types;
using QuantumCore.Game.Packets;

namespace QuantumCore.Game.PacketHandlers.Loading
{
    public class EnterGameHandler : IPacketHandler<EnterGame>
    {
        private readonly ILogger<EnterGameHandler> _logger;

        public EnterGameHandler(ILogger<EnterGameHandler> logger)
        {
            _logger = logger;
        }
        
        public async Task ExecuteAsync(PacketContext<EnterGame> ctx, CancellationToken token = default)
        {
            var player = ctx.Connection.Player;
            if (player == null)
            {
                _logger.LogWarning("Trying to enter game without a player!");
                ctx.Connection.Close();
                return;
            }
            
            // Enable game phase
            await ctx.Connection.SetPhase(EPhases.Game);
            
            await ctx.Connection.Send(new GameTime { Time = (uint) ctx.Connection.Server.ServerTime });
            await ctx.Connection.Send(new Channel { ChannelNo = 1 }); // todo
            
            // Show the player
            await player.Show(ctx.Connection);
            
            // Spawn the player
            if (!await World.World.Instance.SpawnEntity(player))
            {
                _logger.LogWarning("Failed to spawn player entity");
                ctx.Connection.Close();
            }
            
            await player.SendInventory();
            
            // for affects
            await player.SendCharacterUpdate();
        }
    }
}