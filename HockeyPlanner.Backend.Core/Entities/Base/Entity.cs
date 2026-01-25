using System;
using System.Collections.Generic;
using System.Text;

namespace HockeyPlanner.Backend.Core.Entities.Base
{
    public abstract class Entity<TId> : IEquatable<Entity<TId>>
            where TId : notnull
    {
        public TId Id { get; protected set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }

        protected Entity(TId id)
        {
            Id = id;
            CreatedAt = DateTime.UtcNow;
        }

        public override bool Equals(object? obj) =>
            obj is Entity<TId> entity && Id.Equals(entity.Id);

        public bool Equals(Entity<TId>? other) =>
            Equals((object?)other);

        public override int GetHashCode() => Id.GetHashCode();
    }

    public abstract class Entity : Entity<Guid>
    {
        protected Entity() : base(Guid.NewGuid()) { }
        protected Entity(Guid id) : base(id) { }
    }
}
