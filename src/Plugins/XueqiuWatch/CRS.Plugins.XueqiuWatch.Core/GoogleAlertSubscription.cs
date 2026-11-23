namespace CRS.Plugins.XueqiuWatch.Core;

public enum GoogleAlertState { None, External, PendingCreate, Creating, Managed, PendingDelete, Deleting }

public enum GoogleAlertOutcome { Missing, Existing, Created }

public sealed record GoogleAlertSubscription(string AuthorAccount, GoogleAlertState State,
    Guid OperationId = default, string GoogleAccount = "", string RemoteId = "")
{
    public bool ControlsFeed => State is GoogleAlertState.Creating or GoogleAlertState.Managed or
        GoogleAlertState.PendingDelete or GoogleAlertState.Deleting;
}

public sealed record GoogleAlertUpdate(string AuthorAccount, Guid OperationId, string GoogleAccount,
    GoogleAlertOutcome? Outcome = null, string RemoteId = "", string FeedUrl = "");
