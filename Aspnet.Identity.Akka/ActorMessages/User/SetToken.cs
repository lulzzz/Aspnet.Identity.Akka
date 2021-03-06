﻿using Aspnet.Identity.Akka.Interfaces;

namespace Aspnet.Identity.Akka.ActorMessages.User
{
    class SetToken : IUserPropertyChange
    {
        public SetToken(string loginProvider, string name, string value)
        {
            LoginProvider = loginProvider;
            Name = name;
            Value = value;
        }

        public string LoginProvider { get; }
        public string Name { get; }
        public string Value { get; }
    }
}