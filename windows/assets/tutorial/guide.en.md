# Claude Launcher Guide

The launcher manages several Claude accounts on one Windows PC. This guide covers the four core things. Everything stays on your PC, and nothing is sent to Claude.

## Accounts: start, switch, create

![Instances page: one card per account](guide-instances.en.jpg)

- **Start**: when an account isn't running, click Start on its card. The launcher checks your local network setup first and opens Claude after you confirm.
- **Switch**: when an account is running, the button becomes Open and takes you to that account's window. Ctrl+1–Ctrl+0 open accounts too.
- **Create**: click + New instance at the top right. The launcher creates the next free account, up to 10. Then sign in yourself in the Claude window that opens.
- **Manage**: Accounts in the sidebar lists every account in a table you can search, filter and sort. You can also give accounts local aliases.

Each account keeps its own sign-in and sessions. Quitting the launcher doesn't quit Claude.

## Projects: one list for all accounts

![Projects page: add folders, import and sync sessions](guide-projects.en.jpg)

- **Add folder**: put the project folders you use into the list.
- **Detect & import**: find folders from each account's local records. It doesn't read conversations or change account data.
- **Open a project**: choose an account on the project's row. The launcher opens it and copies the path, then select that folder in Claude.

Take turns on a shared project, because the launcher can't detect simultaneous edits. Removing a project from the list doesn't delete files.

## Sync sessions: continue on another account

When an account is running out of quota, you can take a session to another account.

- On the Projects page, click Sync Session…, tick the sessions and pick a target account. Quit the target account normally first.
- What gets written is a cleaned copy with no account details, and the two never merge. A session synced before follows its newest version: syncing again replaces the old version on the other side, and the old version goes to the Recycle Bin.
- The target account must have been opened from the launcher and have created a local session once. For an account the launcher created, once it gets that far and quits, the launcher asks once whether to sync sessions from your other accounts.
- If the target account has signed in to several accounts or organizations, the launcher reads its current organization once after you click sync and writes only into that folder. If it cannot read it, nothing is written.
- Open the target account from the launcher. The session appears in the sidebar, with "Synced" and the time after its title. You send the first message yourself.

Any Claude version works. If the target account's session records change format, the launcher refuses to write and tells you.

## Usage trend: which account next

![Usage trend, below the table on the Accounts page](guide-trend.en.jpg)

- Scroll down on the Accounts page to see each account's 5-hour and weekly usage for the last 7 days.
- The banner at the top suggests which account to use next, and can start or switch to it.
- The numbers only come from what the launcher has seen on screen. It never queries Claude. Recording starts once you open that account's Claude Code.

## See it again

Click Tutorial at the bottom of the sidebar to replay the tour or come back to this guide.
