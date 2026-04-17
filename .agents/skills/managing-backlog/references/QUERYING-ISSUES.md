# Querying Issues

Discover the current backlog state dynamically instead of relying on static snapshots.

## List all open epics with sub-issue status

```powershell
gh api graphql -f query='query {
  repository(owner: "WarHub", name: "battlescribe-spec") {
    issues(first: 20, states: [OPEN], filterBy: { issueType: "Epic" }) {
      nodes {
        number title
        subIssues(first: 50) {
          nodes { number title state }
        }
      }
    }
  }
}'
```

## Get labels and type for a batch of issues

```powershell
gh api graphql -f query='query {
  repository(owner: "WarHub", name: "battlescribe-spec") {
    issues(first: 100, states: [OPEN]) {
      nodes {
        number title
        issueType { name }
        labels(first: 10) { nodes { name } }
        parent { number title }
      }
    }
  }
}'
```

## Find standalone issues (no parent epic)

After querying all open issues, filter for those with `parent: null`.
These are candidates for linking to an existing epic or creating a new one.

## Check if a parent's sub-issues are all closed

```powershell
gh api graphql -f query='query {
  repository(owner: "WarHub", name: "battlescribe-spec") {
    issue(number: PARENT_NUM) {
      title state
      subIssues(first: 100) {
        nodes { number title state }
      }
    }
  }
}'
```

If all sub-issues have `state: "CLOSED"`, the parent can be closed too.
