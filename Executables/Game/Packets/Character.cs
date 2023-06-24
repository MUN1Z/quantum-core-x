﻿using QuantumCore.Core.Packets;
using QuantumCore.Game.Persistence;

namespace QuantumCore.Game.Packets
{
    public class Character
    {
        [Field(0)]
        public uint Id { get; set; }
        [Field(1, Length = 25)]
        public string Name { get; set; }
        [Field(2)]
        public byte Class { get; set; }
        [Field(3)]
        public byte Level { get; set; }
        [Field(4)]
        public uint Playtime { get; set; }
        [Field(5)]
        public byte St { get; set; }
        [Field(6)]
        public byte Ht { get; set; }
        [Field(7)]
        public byte Dx { get; set; }
        [Field(8)]
        public byte Iq { get; set; }
        [Field(9)]
        public ushort BodyPart { get; set; }
        [Field(10)]
        public byte NameChange { get; set; }
        [Field(11)]
        public ushort HairPort { get; set; }
        [Field(12)]
        public uint Unknown { get; set; }
        [Field(13)]
        public int PositionX { get; set; }
        [Field(14)]
        public int PositionY { get; set; }
        [Field(15)]
        public int Ip { get; set; }
        [Field(16)]
        public ushort Port { get; set; }
        [Field(17)]
        public byte SkillGroup { get; set; }

        public static Character FromEntity(Player player)
        {
            return new Character
            {
                Id = 1,
                Name = player.Name,
                Class = player.PlayerClass,
                Level = player.Level,
                Playtime = player.PlayTime,
                St = player.St,
                Ht = player.Ht,
                Dx = player.Dx,
                Iq = player.Iq,
                BodyPart = (ushort) player.BodyPart,
                NameChange = 0,
                HairPort = (ushort) player.HairPart,
                PositionX = player.PositionX,
                PositionY = player.PositionY,
                SkillGroup = player.SkillGroup
            };
        }
    }
}