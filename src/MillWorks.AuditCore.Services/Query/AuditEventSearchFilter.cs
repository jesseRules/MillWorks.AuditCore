using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using MillWorks.AuditCore.Abstractions.Requests;
using MillWorks.AuditCore.EntityFramework.Entities;

namespace MillWorks.AuditCore.Services.Query;

internal static class AuditEventSearchFilter
{
    internal static Expression<Func<AuditEventEntity, bool>> Build(AuditSearchRequest request)
    {
        var terms = AuditWordSearch.BuildTerms(request.SearchTerm);
        Expression<Func<AuditEventEntity, bool>> predicate = e =>
            (!request.StartDate.HasValue || e.InsertedDate >= request.StartDate.Value) &&
            (!request.EndDate.HasValue || e.InsertedDate <= request.EndDate.Value) &&
            (string.IsNullOrWhiteSpace(request.User) || e.User == request.User || e.UserFullName == request.User) &&
            (string.IsNullOrWhiteSpace(request.EventType) || e.EventType == request.EventType) &&
            (string.IsNullOrWhiteSpace(request.EntityType) || e.EntityType == request.EntityType) &&
            (!request.EventId.HasValue || e.EventId == request.EventId.Value) &&
            (string.IsNullOrWhiteSpace(request.EntityId) || e.EntityId == request.EntityId);
        foreach (var term in terms)
        {
            Expression<Func<AuditEventEntity, bool>> words = e =>
                EF.Functions.Contains(e.JsonData!, term, 0) ||
                EF.Functions.Contains(e.User!, term, 0) ||
                EF.Functions.Contains(e.UserFullName!, term, 0) ||
                EF.Functions.Contains(e.EventType!, term, 0) ||
                EF.Functions.Contains(e.EntityType!, term, 0) ||
                EF.Functions.Contains(e.EntityId!, term, 0) ||
                EF.Functions.Contains(e.AdditionalData!, term, 0);
            var parameter = predicate.Parameters[0];
            var body = new ParameterSubstitution(words.Parameters[0], parameter).Visit(words.Body)!;
            predicate = Expression.Lambda<Func<AuditEventEntity, bool>>(Expression.AndAlso(predicate.Body, body), parameter);
        }
        return predicate;
    }

    private sealed class ParameterSubstitution(ParameterExpression source, ParameterExpression target) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == source ? target : node;
    }
}
