﻿using Akka.Actor;
using Aspnet.Identity.Akka.ActorMessages;
using Aspnet.Identity.Akka.ActorMessages.User;
using Aspnet.Identity.Akka.ActorMessages.UserCoordinator;
using Aspnet.Identity.Akka.Actors;
using Aspnet.Identity.Akka.Interfaces;
using Aspnet.Identity.Akka.Model;
using Microsoft.AspNetCore.Identity;
using System;
using System.Collections.Generic;
using System.Security.Claims;

namespace Aspnet.Identity.Akka.ActorHelpers
{
    public class UserCoordinatorHelper<TKey, TUser>
        where TKey : IEquatable<TKey>
        where TUser : IdentityUser<TKey>
    {
        public UserCoordinatorHelper(Func<TKey, IActorRef> createUserActor, Func<IActorContext> getContext)
        {
            this.createUserActor = createUserActor;
            this.getContext = getContext;
        }

        private bool inSync;

        //useractors, as created by the real user coordinators
        private Dictionary<TKey, IActorRef> userActors = new Dictionary<TKey, IActorRef>();

        //just to find users by key, username, email and external login.
        private Dictionary<string, TKey> existingIds = new Dictionary<string, TKey>();
        private Dictionary<string, TKey> existingUserNames = new Dictionary<string, TKey>();
        private Dictionary<string, TKey> existingEmails = new Dictionary<string, TKey>();
        private Dictionary<ExternalLogin, TKey> existingLogins = new Dictionary<ExternalLogin, TKey>();
        private Dictionary<string, Dictionary<string, HashSet<TKey>>> usersByClaims = new Dictionary<string, Dictionary<string, HashSet<TKey>>>();

        //and a reverse lookup
        private Dictionary<TKey, Tuple<string, string, string, HashSet<ExternalLogin>, HashSet<Claim>>> reverseLookup = new Dictionary<TKey, Tuple<string, string, string, HashSet<ExternalLogin>, HashSet<Claim>>>();

        private readonly Func<TKey, IActorRef> createUserActor;
        private readonly Func<IActorContext> getContext;
        private List<Tuple<IActorRef, ICommand, Action<IEvent, Action<IEvent>>>> stash = new List<Tuple<IActorRef, ICommand, Action<IEvent, Action<IEvent>>>>();

        public virtual void SetInSync(bool inSync)
        {
            this.inSync = inSync;
            foreach (var cmd in stash)
            {
                OnCommand(cmd.Item1, cmd.Item2, cmd.Item3);
            }
            stash.Clear();
        }

        public virtual void OnCommand(IActorRef sender, ICommand message, Action<IEvent, Action<IEvent>> persist)
        {
            if (!inSync)
            {
                stash.Add(new Tuple<IActorRef, ICommand, Action<IEvent, Action<IEvent>>>(sender, message, persist));
                return;
            }

            switch (message)
            {
                case NotifyUserEvent cmd:
                    persist(cmd.Evt, e => OnEvent(sender, e));
                    break;
                case RequestClaims<TKey> req:
                    ForwardUserRequests(req.UserId, req);
                    break;
                case RequestTokens<TKey> req:
                    ForwardUserRequests(req.UserId, req);
                    break;
                case RequestUserLoginInfo<TKey> req:
                    ForwardUserRequests(req.UserId, req);
                    break;
                case FindById fbi:
                    FindById(fbi.UserId, sender);
                    break;
                case FindByEmail fbe:
                    FindByEmail(fbe.NormalizedEmail, sender);
                    break;
                case FindByClaim fbc:
                    FindByClaim(fbc.Type, fbc.Value, sender);
                    break;
                case FindByLogin fbl:
                    FindByLogin(fbl.ExternalLogin, sender);
                    break;
                case FindByUsername fbu:
                    FindByUsername(fbu.NormalizedUsername, sender);
                    break;
                case CreateUser<TKey, TUser> cu:
                    CreateUser(cu, sender);
                    break;
                case DeleteUser<TKey> du:
                    DeleteUser(du, sender);
                    break;
                case UpdateUser<TKey, TUser> uu:
                    UpdateUser(uu, sender);
                    break;
            }
        }

        private void UpdateUser(UpdateUser<TKey, TUser> uu, IActorRef sender)
        {
            var errors = new List<IdentityError>();
            foreach (var change in uu.User.Changes)
            {
                switch (change)
                {
                    case UserNameChanged evt:
                        {
                            if (existingUserNames.TryGetValue(evt.UserName.ToUpperInvariant(), out TKey otherId) && !otherId.Equals(uu.Key))
                            {
                                errors.Add(new IdentityError { Description = "Duplicate Username found." });
                            }
                        }
                        break;
                    case NormalizedEmailChanged evt:
                        {
                            if (existingEmails.TryGetValue(evt.Email, out TKey otherId) && !otherId.Equals(uu.Key))
                            {
                                errors.Add(new IdentityError { Description = "Duplicate Email found." });
                            }
                        }
                        break;
                    case UserLoginInfoAdded evt:
                        {
                            if (existingLogins.TryGetValue(new ExternalLogin(evt.LoginProvider, evt.ProviderKey), out TKey otherId) && !otherId.Equals(uu.Key))
                            {
                                errors.Add(new IdentityError { Description = "Duplicate Login found." });
                            }
                        }
                        break;
                }
            }
            if (errors.Count > 0)
            {
                sender.Tell(IdentityResult.Failed(errors.ToArray()));
            }
            else
            {
                if (!userActors.TryGetValue(uu.Key, out IActorRef actor))
                {
                    userActors[uu.Key] = actor = createUserActor(uu.Key);
                }
                actor.Tell(uu, sender);
            }
        }

        private void DeleteUser(DeleteUser<TKey> du, IActorRef sender)
        {
            if (userActors.ContainsKey(du.UserId) || existingIds.ContainsKey(du.UserId.ToString()))
            {
                if (!userActors.TryGetValue(du.UserId, out IActorRef actor))
                {
                    userActors[du.UserId] = actor = createUserActor(du.UserId);
                }
                actor.Tell(du, sender);
            }
            else
            {
                sender.Tell(IdentityResult.Failed());
            }
        }

        private void CreateUser(CreateUser<TKey, TUser> cu, IActorRef sender)
        {
            var prelim = TestCreateUser(cu);
            if (prelim.Succeeded)
            {
                var actor = userActors[cu.User.Id] = createUserActor(cu.User.Id);
                actor.Tell(cu, sender);
            }
            else
            {
                sender.Tell(prelim);
            }
        }

        //beware!
        //race conditions might concur here
        //maybe lock email/ username somehow, with a timer
        private IdentityResult TestCreateUser(CreateUser<TKey, TUser> cu)
        {
            var usr = cu.User;
            var errors = new List<IdentityError>();
            if (existingUserNames.ContainsKey(usr.NormalizedUserName)) errors.Add(new IdentityError { Description = "Username already exists" });
            if (existingUserNames.ContainsKey(usr.UserName.ToUpperInvariant())) errors.Add(new IdentityError { Description = "Username already exists" });
            if (existingEmails.ContainsKey(usr.NormalizedEmail)) errors.Add(new IdentityError { Description = "Email already exists" });
            if (existingIds.ContainsKey(usr.Id.ToString())) errors.Add(new IdentityError { Description = "UserId already present" });
            foreach (var login in usr.Logins)
            {
                if (existingLogins.ContainsKey(new ExternalLogin(login.LoginProvider, login.ProviderKey))) errors.Add(new IdentityError { Description = "External login already present" });
            }

            if (errors.Count > 0)
            {
                return IdentityResult.Failed(errors.ToArray());
            } else
            {
                return IdentityResult.Success;
            }
        }

        private void FindByUsername(string normalizedUsername, IActorRef sender)
        {
            if (existingUserNames.TryGetValue(normalizedUsername, out TKey key))
            {
                if (!userActors.TryGetValue(key, out IActorRef actor))
                {
                    userActors[key] = actor = createUserActor(key);
                }
                actor.Tell(ReturnDetails.Instance, sender);
            }
            sender.Tell(NilMessage.Instance);
        }

        private void FindByLogin(ExternalLogin external, IActorRef sender)
        {
            if (existingLogins.TryGetValue(external, out TKey key))
            {
                if (!userActors.TryGetValue(key, out IActorRef actor))
                {
                    userActors[key] = actor = createUserActor(key);
                }
                actor.Tell(ReturnDetails.Instance, sender);
            }
            sender.Tell(NilMessage.Instance);
        }

        private void FindById(string userId, IActorRef sender)
        {
            if (existingIds.TryGetValue(userId, out TKey key))
            {
                if (!userActors.TryGetValue(key, out IActorRef actor))
                {
                    userActors[key] = actor = createUserActor(key);
                }
                actor.Tell(ReturnDetails.Instance, sender);
            }
            sender.Tell(NilMessage.Instance);
        }

        private void FindByEmail(string email, IActorRef sender)
        {
            if (existingEmails.TryGetValue(email, out TKey key))
            {
                if (!userActors.TryGetValue(key, out IActorRef actor))
                {
                    userActors[key] = actor = createUserActor(key);
                }
                actor.Tell(ReturnDetails.Instance, sender);
            }
            sender.Tell(NilMessage.Instance);
        }

        private void FindByClaim(string type, string value, IActorRef sender)
        {
            //usersByClaims
            if (usersByClaims.TryGetValue(type, out Dictionary<string, HashSet<TKey>> byType)
                && byType.TryGetValue(value, out HashSet<TKey> users))
            {
                var actors = new List<IActorRef>();

                foreach (var key in users)
                {
                    if (!userActors.TryGetValue(key, out IActorRef actor))
                    {
                        userActors[key] = actor = createUserActor(key);
                    }

                    actors.Add(actor);
                    //actor.Tell(ReturnDetails.Instance, sender);
                }
                if (actors.Count > 0)
                {
                    var context = getContext();
                    var aggregator = context.ActorOf(Props.Create(() => new Aggregator<TUser>(actors)));
                    aggregator.Ask<AggregatedReply<TUser>>(ReturnDetails.Instance).PipeTo(sender);
                    return;
                }
            }
            sender.Tell(new TUser[0]);
        }

        private void ForwardUserRequests(TKey userId, IGetUserProperties req)
        {
            if (!userActors.TryGetValue(userId, out IActorRef actor))
            {
                userActors[userId] = actor = createUserActor(userId);
            }
            actor.Forward(req);
        }

        /// <summary>
        /// These events are raised by the user actor, or by the persistence layer
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="message"></param>
        public virtual void OnEvent(IActorRef sender, IEvent message)
        {
            switch (message)
            {
                //when events are not meant for the coordinator, but are persisted in the coordinator
                case IUserEvent<TKey> evt:
                    break;
                case UserCreated<TKey> evt:
                    break;
                case UserDeleted<TKey> evt:
                    break;
                case UserNameChanged<TKey> evt:
                    break;
                case UserLoginAdded<TKey> evt:
                    break;
                case UserLoginRemoved<TKey> evt:
                    break;
                case UserEmailChanged<TKey> evt:
                    break;
                case UserClaimsAdded<TKey> evt:
                    break;
                case UserClaimsRemoved<TKey> evt:
                    break;
            }
        }
    }
}
