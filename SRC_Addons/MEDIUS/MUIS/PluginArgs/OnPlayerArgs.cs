﻿using PSMultiServer.Addons.Medius.MUIS.Models;

namespace PSMultiServer.Addons.Medius.MUIS.PluginArgs
{
    public class OnPlayerArgs
    {
        /// <summary>
        /// Player.
        /// </summary>
        public ClientObject Player { get; set; }

        public override string ToString()
        {
            return base.ToString() + " " +
                $"Player: {Player}";
        }
    }
}
