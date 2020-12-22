using System.Collections.Generic;
using System.Linq;
using QuantumCore.Core.Types;
using Serilog;

namespace QuantumCore.Game
{
    /// <summary>
    /// Manage all static data related to monster
    /// </summary>
    public static class MonsterManager
    {
        private static MobProto _proto;
        
        /// <summary>
        /// Try to load mob_proto file
        /// </summary>
        public static void Load()
        {
            _proto = MobProto.FromFile("data/mob_proto");
            Log.Debug($"Loaded {_proto.Content.Data.Monsters.Count} monsters");
        }

        public static MobProto.Monster GetMonster(uint id)
        {
            return _proto.Content.Data.Monsters.FirstOrDefault(monster => monster.Id == id);
        }

        public static List<MobProto.Monster> GetMonsters()
        {
            return _proto.Content.Data.Monsters;
        }
    }
}