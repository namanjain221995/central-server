# Company Portal

Employees install approved software themselves. An administrator puts a
package in the catalog, chooses who may see it and publishes it; a small
desktop app on every managed PC shows the published tiles, and clicking
Install queues the same install an administrator would have queued. This
document is the contract: what the catalog is, how a request is decided, and
what is deliberately not promised.

It builds on software deployment (packages, `InstallPackage` tasks, the hash
and signer pins the agent applies) and on [device-groups.md](device-groups.md);
it changes neither.

## Two sides, one decision

**The administrator's side** is the Company Apps page. An entry names one
approved package (`software_packages`) and adds what a user needs to choose it:
a display name, a line of description, a category, a PNG icon. It is visible to
every device or to selected groups. New entries start **unpublished**, so an
entry can be set up before anyone sees it. Changing the catalog needs
`software.deploy`, because publishing pre-approves an install on every device
that can see the entry -- the same authority as deploying the package.

**The device's side** is the portal. It authenticates with the device
credential, like everything else the agent does, and can do three things:
list what this device may see, fetch an icon, and ask for an entry by id. That
is the whole surface. It never sees a file name, a hash or a product code, and
it cannot name a package, a path or an argument. The one thing a request
carries is a catalog app id.

**The server decides.** A request is honoured only if the entry is published,
backed by a package that is not withdrawn, and visible to the device's group,
and then only if the device needs it. The install is queued through
`SoftwareDeploymentService` exactly as an administrator's deployment would be:
eligibility, the retired-device rule, the minimum agent version, and the
package's SHA-256 and signer pins all apply unchanged. There is no second
install path.

## Visibility

`AllDevices` means every device of the organization. `SelectedGroups` means
devices whose group is one of the linked groups. The built-in "All Devices"
group is **not** a way to say "everyone": it holds only devices in no other
group, so linking it would hide the entry from every device that has been put
in a department. Use `AllDevices` for that.

A device in exactly one group is the model everywhere in the platform; the
portal does not add membership of its own. Move a device between groups on the
Groups page and its portal changes at the next refresh.

## What a request comes to

| Outcome | Meaning |
|---|---|
| `Queued` | An `InstallPackage` task was created. Progress is the task's; the portal shows "Installing" until the next inventory reports the product, or the task fails. |
| `AlreadyInstalled` | The device's inventory already reports the package (same or newer version). Nothing was sent. |
| `AlreadyInProgress` | An install of the same package is queued or running on the device, from the portal or from an administrator. |
| `Refused` | Everything else: unpublished, not visible to this device, package withdrawn, agent too old, device retired, or no such entry. **One value on purpose**, so the portal cannot be used to find out what other groups are offered. |

Every request, refusals included, is written to `catalog_install_requests`
with the device, the reported user, the outcome and the task, and audited as
`software.catalog.request` by the device (`AuditActorType.Agent`). A run of
refusals from one machine is worth noticing, which is why they are kept. A
device that sends more than 10 requests in a minute gets `Refused` without a
row: a person clicks a tile a few times, a loop does not deserve a row each.

## Who authorised the install

Every task row and audit entry needs a platform user. A self-service install
is attributed to **the administrator who published the entry**: publishing is
the approval the install rests on. The requesting Windows user is carried in
the task's display text -- `Company Portal (DOMAIN\user)` -- and in the request
record, never as the actor. The name is whatever the agent reports; it decides
nothing and is not an authenticated identity.

## What is deliberately not promised

- **No approval step.** A published entry installs on click. If a package
  should need a decision each time, do not publish it; deploy it.
- **No uninstall.** The portal installs. Removing software is an
  administrator's action, through the inventory.
- **MSI only.** The catalog offers what the package catalogue holds, and that
  is Windows Installer packages verified by hash and signer. Chrome, Slack and
  Zoom all ship enterprise MSIs. An `.exe` installer with its own silent
  switches is a different trust decision and a different installer path; it is
  not this feature.
- **The user name is informational.** See above.
- **No portal-side caching of decisions.** The portal asks the server every
  time; a change in the catalog is effective at the next request.

## Removing an entry

Deleting an entry removes the tile. Installs already made from it are
untouched, and their request history keeps the app's name. Withdrawing the
package behind an entry hides the entry from every device without deleting
it; restoring the package shows it again, if it is published. Publishing an
entry while its package is withdrawn is refused: "published" on the Company
Apps page must mean employees can see it.
