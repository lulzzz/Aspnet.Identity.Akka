﻿using Akka.Actor;
using Aspnet.Identity.Akka.ActorHelpers;
using Aspnet.Identity.Akka.ActorMessages;
using Aspnet.Identity.Akka.Interfaces;
using System;

namespace Aspnet.Identity.Akka.Actors
{
    public class UserActor<TKey, TUser> : UntypedActor
        where TKey : IEquatable<TKey>
        where TUser : IdentityUser<TKey>
    {
        private UserHelper<TKey, TUser> userHelper;

        public UserActor(TKey userId, IActorRef coordinator)
        {
            userHelper = new UserHelper<TKey, TUser>(userId, coordinator);
        }

        protected override void OnReceive(object message)
        {
            switch (message)
            {
                case InSyncCommand insync:
                    userHelper.SetInSync(insync.IsSynchronized);
                    break;
                case IEvent @event:
                    userHelper.OnEvent(Sender, @event);
                    break;
                case ICommand command:
                    HandleCommand(command);
                    break;
            }
        }

        protected virtual void HandleCommand(ICommand command)
        {
            userHelper.OnCommand(Sender, command, (evt, a) => a(evt));
        }
    }
}
