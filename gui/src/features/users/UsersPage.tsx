import { useEffect, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { readLocalStorageState, useLocalStorageState } from "@/hooks/useLocalStorageState";
import { cn } from "@/lib/utils";
import { toast } from "sonner";
import { Loader2, MoreVertical, UserPlus } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle,
} from "@/components/ui/dialog";
import {
  DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuSeparator, DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import {
  Sheet, SheetContent, SheetFooter, SheetHeader, SheetTitle,
} from "@/components/ui/sheet";
import { useAuth } from "../../auth/AuthContext";
import { isApiError } from "../../api/client";
import { userApi } from "../../api/endpoints";
import type { Role, User } from "../../api/types";
import { ConfirmDialog } from "../../components/ConfirmDialog";
import { CorrelationError } from "../../components/CorrelationError";
import { activeFilterClass, FilterBar } from "../../components/FilterBar";
import { Page } from "../../components/Page";
import { PageHeader } from "../../components/PageHeader";
import { PagedTable, type Column } from "../../components/PagedTable";
import { RelativeTime } from "../../components/RelativeTime";
import { ActiveBadge } from "../../components/StatusBadge";

const MIN_PASSWORD_LENGTH = 12;

/** One error-to-text mapping for every toast on this page (the API's detail wins over a generic title). */
function errorText(error: unknown): string {
  if (isApiError(error)) {
    return error.detail ?? error.title;
  }

  return error instanceof Error ? error.message : String(error);
}

/** The role options for a select: the server's roles, plus the current value so an unknown role still shows. */
function roleNames(roles: Role[], current?: string): string[] {
  const names = roles.map((role) => role.name);
  if (current !== undefined && !names.includes(current)) {
    names.push(current);
  }

  return names;
}

function ChangeRoleDialog({ user, roles, onClose }: { user: User; roles: Role[]; onClose: () => void }) {
  const queryClient = useQueryClient();
  const [role, setRole] = useState(user.role);

  const setRoleMutation = useMutation({
    mutationFn: () => userApi.setRole(user.id, role),
    onSuccess: () => {
      toast.success(`Role updated for ${user.username}.`);
      void queryClient.invalidateQueries({ queryKey: ["users"] });
      onClose();
    },
    onError: (error) => toast.error(errorText(error)),
  });

  return (
    <Dialog
      open
      onOpenChange={(next) => {
        if (!next && !setRoleMutation.isPending) {
          onClose();
        }
      }}
    >
      <DialogContent className="sm:max-w-sm" data-testid="user-set-role-dialog">
        <DialogHeader>
          <DialogTitle>Change role for {user.username}</DialogTitle>
        </DialogHeader>
        <div className="flex flex-col gap-1.5">
          <Label>Role</Label>
          <Select value={role} onValueChange={setRole}>
            <SelectTrigger size="sm" className="h-8 w-full" data-testid="user-set-role-select">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {roleNames(roles, user.role).map((name) => (
                <SelectItem key={name} value={name}>{name}</SelectItem>
              ))}
            </SelectContent>
          </Select>
        </div>
        <DialogFooter>
          <Button
            variant="ghost"
            size="sm"
            onClick={onClose}
            disabled={setRoleMutation.isPending}
            data-testid="user-set-role-cancel"
          >
            Cancel
          </Button>
          <Button
            size="sm"
            onClick={() => setRoleMutation.mutate()}
            disabled={setRoleMutation.isPending}
            data-testid="user-set-role-submit"
          >
            {setRoleMutation.isPending && <Loader2 className="animate-spin" />}
            Save
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

function ResetPasswordDialog({ user, onClose }: { user: User; onClose: () => void }) {
  const queryClient = useQueryClient();
  const [password, setPassword] = useState("");

  const setPasswordMutation = useMutation({
    mutationFn: () => userApi.setPassword(user.id, password),
    onSuccess: () => {
      toast.success(`Password reset for ${user.username}.`);
      void queryClient.invalidateQueries({ queryKey: ["users"] });
      onClose();
    },
    onError: (error) => toast.error(errorText(error)),
  });

  const valid = password.length >= MIN_PASSWORD_LENGTH;
  const showInvalid = password.length > 0 && !valid;

  return (
    <Dialog
      open
      onOpenChange={(next) => {
        if (!next && !setPasswordMutation.isPending) {
          onClose();
        }
      }}
    >
      <DialogContent className="sm:max-w-sm" data-testid="user-set-password-dialog">
        <DialogHeader>
          <DialogTitle>Reset password for {user.username}</DialogTitle>
        </DialogHeader>
        <div className="flex flex-col gap-1.5">
          <Label htmlFor="user-set-password-input">New password</Label>
          <Input
            id="user-set-password-input"
            type="password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            aria-invalid={showInvalid || undefined}
            className="h-8"
            data-testid="user-set-password-input"
          />
          <p className={showInvalid ? "text-xs text-destructive" : "text-xs text-muted-foreground"}>
            At least {MIN_PASSWORD_LENGTH} characters
          </p>
        </div>
        <DialogFooter>
          <Button
            variant="ghost"
            size="sm"
            onClick={onClose}
            disabled={setPasswordMutation.isPending}
            data-testid="user-set-password-cancel"
          >
            Cancel
          </Button>
          <Button
            size="sm"
            onClick={() => setPasswordMutation.mutate()}
            disabled={!valid || setPasswordMutation.isPending}
            data-testid="user-set-password-submit"
          >
            {setPasswordMutation.isPending && <Loader2 className="animate-spin" />}
            Reset
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

/**
 * Editing an existing account's identity fields in a right side sheet (DESIGN.md 7.4), the counterpart of the
 * create form. The sign-in name is only editable for a local user: an SSO account's username is its Entra
 * account name, refreshed from the token at every sign-in, so the field is locked and says why.
 */
function EditUserSheet({ user, onClose }: { user: User; onClose: () => void }) {
  const queryClient = useQueryClient();
  const [username, setUsername] = useState(user.username);
  const [email, setEmail] = useState(user.email ?? "");
  const [displayName, setDisplayName] = useState(user.displayName ?? "");
  const sso = user.provider !== "local";

  const save = useMutation({
    mutationFn: () => userApi.updateProfile(user.id, {
      username: username.trim(),
      email: email.trim() === "" ? null : email.trim(),
      displayName: displayName.trim() === "" ? null : displayName.trim(),
    }),
    onSuccess: (updated) => {
      toast.success(`User ${updated.username} updated.`);
      void queryClient.invalidateQueries({ queryKey: ["users"] });
      onClose();
    },
    onError: (error) => toast.error(errorText(error)),
  });

  const canSubmit = username.trim() !== "" && !save.isPending;

  return (
    <Sheet
      open
      onOpenChange={(next) => {
        if (!next && !save.isPending) {
          onClose();
        }
      }}
    >
      <SheetContent className="w-full sm:max-w-xl" data-testid="edit-user-dialog">
        <SheetHeader>
          <SheetTitle>Edit {user.username}</SheetTitle>
        </SheetHeader>
        <div className="flex flex-1 flex-col gap-4 overflow-y-auto px-4">
          {save.isError && isApiError(save.error) && <CorrelationError error={save.error} />}
          {save.isError && !isApiError(save.error) && (
            <p className="text-[13px] text-destructive">{String(save.error)}</p>
          )}

          <div className="flex flex-col gap-1.5">
            <Label htmlFor="edit-user-username">Username</Label>
            <Input
              id="edit-user-username"
              required
              disabled={sso}
              value={username}
              onChange={(e) => setUsername(e.target.value)}
              className="h-8"
              data-testid="edit-user-username"
            />
            {sso && (
              <p className="text-xs text-muted-foreground">
                The sign-in name comes from Microsoft Entra ID and is refreshed at each sign-in.
              </p>
            )}
          </div>
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="edit-user-displayname">Display name</Label>
            <Input
              id="edit-user-displayname"
              value={displayName}
              onChange={(e) => setDisplayName(e.target.value)}
              className="h-8"
              data-testid="edit-user-displayname"
            />
          </div>
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="edit-user-email">Email</Label>
            <Input
              id="edit-user-email"
              type="email"
              value={email}
              onChange={(e) => setEmail(e.target.value)}
              className="h-8"
              data-testid="edit-user-email"
            />
            {sso && (
              <p className="text-xs text-muted-foreground">
                Entra refreshes the display name and email it carries at the next sign-in.
              </p>
            )}
          </div>
        </div>
        <SheetFooter className="flex-row justify-end">
          <Button
            variant="ghost"
            size="sm"
            onClick={onClose}
            disabled={save.isPending}
            data-testid="edit-user-cancel"
          >
            Cancel
          </Button>
          <Button
            size="sm"
            onClick={() => save.mutate()}
            disabled={!canSubmit}
            data-testid="edit-user-submit"
          >
            {save.isPending && <Loader2 className="animate-spin" />}
            Save
          </Button>
        </SheetFooter>
      </SheetContent>
    </Sheet>
  );
}

/** The create-user form in a right side sheet (DESIGN.md 7.4); the old dialog's testid stays on the content. */
function CreateUserSheet({ roles, onClose }: { roles: Role[]; onClose: () => void }) {
  const queryClient = useQueryClient();
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [role, setRole] = useState("viewer");
  const [email, setEmail] = useState("");
  const [displayName, setDisplayName] = useState("");

  const create = useMutation({
    mutationFn: () => userApi.create({
      username: username.trim(),
      password,
      role,
      email: email.trim() === "" ? null : email.trim(),
      displayName: displayName.trim() === "" ? null : displayName.trim(),
    }),
    onSuccess: (created) => {
      toast.success(`User ${created.username} created.`);
      void queryClient.invalidateQueries({ queryKey: ["users"] });
      onClose();
    },
    onError: (error) => toast.error(errorText(error)),
  });

  const passwordValid = password.length >= MIN_PASSWORD_LENGTH;
  const showPasswordInvalid = password.length > 0 && !passwordValid;
  const canSubmit = username.trim() !== "" && passwordValid && role !== "" && !create.isPending;

  return (
    <Sheet
      open
      onOpenChange={(next) => {
        if (!next && !create.isPending) {
          onClose();
        }
      }}
    >
      <SheetContent className="w-full sm:max-w-xl" data-testid="create-user-dialog">
        <SheetHeader>
          <SheetTitle>Create user</SheetTitle>
        </SheetHeader>
        <div className="flex flex-1 flex-col gap-4 overflow-y-auto px-4">
          {create.isError && isApiError(create.error) && <CorrelationError error={create.error} />}
          {create.isError && !isApiError(create.error) && (
            <p className="text-[13px] text-destructive">{String(create.error)}</p>
          )}

          <div className="flex flex-col gap-1.5">
            <Label htmlFor="create-user-username">Username</Label>
            <Input
              id="create-user-username"
              required
              value={username}
              onChange={(e) => setUsername(e.target.value)}
              className="h-8"
              data-testid="create-user-username"
            />
          </div>
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="create-user-password">Password</Label>
            <Input
              id="create-user-password"
              type="password"
              required
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              aria-invalid={showPasswordInvalid || undefined}
              className="h-8"
              data-testid="create-user-password"
            />
            <p className={showPasswordInvalid ? "text-xs text-destructive" : "text-xs text-muted-foreground"}>
              At least {MIN_PASSWORD_LENGTH} characters
            </p>
          </div>
          <div className="flex flex-col gap-1.5">
            <Label>Role</Label>
            <Select value={role} onValueChange={setRole}>
              <SelectTrigger size="sm" className="h-8 w-full" data-testid="create-user-role">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {roleNames(roles, "viewer").map((name) => (
                  <SelectItem key={name} value={name}>{name}</SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="create-user-email">Email</Label>
            <Input
              id="create-user-email"
              type="email"
              value={email}
              onChange={(e) => setEmail(e.target.value)}
              className="h-8"
              data-testid="create-user-email"
            />
          </div>
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="create-user-displayname">Display name</Label>
            <Input
              id="create-user-displayname"
              value={displayName}
              onChange={(e) => setDisplayName(e.target.value)}
              className="h-8"
              data-testid="create-user-displayname"
            />
          </div>
        </div>
        <SheetFooter className="flex-row justify-end">
          <Button
            variant="ghost"
            size="sm"
            onClick={onClose}
            disabled={create.isPending}
            data-testid="create-user-cancel"
          >
            Cancel
          </Button>
          <Button
            size="sm"
            onClick={() => create.mutate()}
            disabled={!canSubmit}
            data-testid="create-user-submit"
          >
            {create.isPending && <Loader2 className="animate-spin" />}
            Create
          </Button>
        </SheetFooter>
      </SheetContent>
    </Sheet>
  );
}

/** User administration: list/filter accounts, create local users, edit an account's identity fields, and manage
 * role, password, active state, and permanent removal. */
export default function UsersPage() {
  const queryClient = useQueryClient();
  const [usernameInput, setUsernameInput] = useLocalStorageState("sqlflow.filters.users.username", "");
  // Seed the debounced value from the same remembered text so the first query runs filtered, with no flash.
  const [usernameFilter, setUsernameFilter] = useState(() =>
    readLocalStorageState("sqlflow.filters.users.username", "").trim());
  const [roleFilter, setRoleFilter] = useLocalStorageState("sqlflow.filters.users.role", "");
  const [providerFilter, setProviderFilter] = useLocalStorageState("sqlflow.filters.users.provider", "");
  const [activeFilter, setActiveFilter] = useLocalStorageState("sqlflow.filters.users.active", "");
  const [createOpen, setCreateOpen] = useState(false);
  const [editUser, setEditUser] = useState<User | null>(null);
  const [roleDialogUser, setRoleDialogUser] = useState<User | null>(null);
  const [passwordDialogUser, setPasswordDialogUser] = useState<User | null>(null);
  const [deactivateUser, setDeactivateUser] = useState<User | null>(null);
  const [deleteUser, setDeleteUser] = useState<User | null>(null);
  // The signed-in account cannot delete itself (the server refuses too); the menu says so rather than failing late.
  const { session } = useAuth();

  // The username filter debounces keystrokes so each pause, not each character, costs an API call.
  useEffect(() => {
    const timer = window.setTimeout(() => setUsernameFilter(usernameInput.trim()), 400);
    return () => window.clearTimeout(timer);
  }, [usernameInput]);

  const rolesQuery = useQuery({ queryKey: ["roles"], queryFn: userApi.roles });
  const roles = rolesQuery.data ?? [];

  const activate = useMutation({
    mutationFn: (user: User) => userApi.activate(user.id),
    onSuccess: (updated) => {
      toast.success(`User ${updated.username} activated.`);
      void queryClient.invalidateQueries({ queryKey: ["users"] });
    },
    onError: (error) => toast.error(errorText(error)),
  });

  const deactivate = useMutation({
    mutationFn: (user: User) => userApi.deactivate(user.id),
    onSuccess: (updated) => {
      toast.success(`User ${updated.username} deactivated.`);
      void queryClient.invalidateQueries({ queryKey: ["users"] });
      setDeactivateUser(null);
    },
    onError: (error) => {
      toast.error(errorText(error));
      setDeactivateUser(null);
    },
  });

  const remove = useMutation({
    mutationFn: (user: User) => userApi.remove(user.id),
    onSuccess: (_result, user) => {
      toast.success(`User ${user.username} deleted.`);
      void queryClient.invalidateQueries({ queryKey: ["users"] });
      setDeleteUser(null);
    },
    onError: (error) => {
      toast.error(errorText(error));
      setDeleteUser(null);
    },
  });

  const columns: Column<User>[] = [
    {
      id: "username",
      header: "Username",
      render: (row) => <span className="font-mono text-[12px] font-medium">{row.username}</span>,
    },
    { id: "displayName", header: "Display name", render: (row) => row.displayName ?? "-" },
    { id: "role", header: "Role", render: (row) => <Badge variant="secondary">{row.role}</Badge> },
    { id: "provider", header: "Provider", render: (row) => <Badge variant="outline">{row.provider}</Badge> },
    { id: "status", header: "Status", render: (row) => <ActiveBadge active={row.active} /> },
    { id: "lastLogin", header: "Last login", render: (row) => <RelativeTime value={row.lastLoginUtc} /> },
    { id: "created", header: "Created", render: (row) => <RelativeTime value={row.createdUtc} /> },
    {
      id: "actions",
      header: "Actions",
      align: "right",
      render: (row) => (
        <DropdownMenu>
          <DropdownMenuTrigger asChild>
            <Button
              variant="ghost"
              size="icon-xs"
              aria-label={`Actions for ${row.username}`}
              onClick={(e) => e.stopPropagation()}
              data-testid="user-actions"
            >
              <MoreVertical />
            </Button>
          </DropdownMenuTrigger>
          <DropdownMenuContent align="end" data-testid="user-actions-menu">
            <DropdownMenuItem
              onClick={() => setEditUser(row)}
              data-testid="user-menu-edit"
            >
              Edit user
            </DropdownMenuItem>
            <DropdownMenuItem
              onClick={() => setRoleDialogUser(row)}
              data-testid="user-menu-change-role"
            >
              Change role
            </DropdownMenuItem>
            {row.provider === "local" ? (
              <DropdownMenuItem
                onClick={() => setPasswordDialogUser(row)}
                data-testid="user-menu-reset-password"
              >
                Reset password
              </DropdownMenuItem>
            ) : (
              <DropdownMenuItem disabled data-testid="user-menu-reset-password">
                <span className="flex flex-col">
                  Reset password
                  <span className="text-[11px] text-muted-foreground">Managed in Microsoft Entra ID</span>
                </span>
              </DropdownMenuItem>
            )}
            {row.active ? (
              <DropdownMenuItem
                onClick={() => setDeactivateUser(row)}
                data-testid="user-menu-deactivate"
              >
                Deactivate
              </DropdownMenuItem>
            ) : (
              <DropdownMenuItem
                onClick={() => activate.mutate(row)}
                disabled={activate.isPending}
                data-testid="user-menu-activate"
              >
                Activate
              </DropdownMenuItem>
            )}
            <DropdownMenuSeparator />
            {row.username === session?.subject ? (
              <DropdownMenuItem disabled data-testid="user-menu-delete">
                <span className="flex flex-col">
                  Delete user
                  <span className="text-[11px] text-muted-foreground">This is your own account</span>
                </span>
              </DropdownMenuItem>
            ) : (
              <DropdownMenuItem
                variant="destructive"
                onClick={() => setDeleteUser(row)}
                data-testid="user-menu-delete"
              >
                Delete user
              </DropdownMenuItem>
            )}
          </DropdownMenuContent>
        </DropdownMenu>
      ),
    },
  ];

  return (
    <Page data-testid="page-users">
      <PageHeader
        title="Users"
        actions={(
          <Button size="sm" onClick={() => setCreateOpen(true)} data-testid="open-create-user">
            <UserPlus />
            Create user
          </Button>
        )}
      />

      <FilterBar>
        <Input
          value={usernameInput}
          onChange={(e) => setUsernameInput(e.target.value)}
          placeholder="Filter by username"
          aria-label="Username"
          className={cn("h-8 w-56", usernameInput !== "" && activeFilterClass)}
          data-testid="filter-username"
        />
        <Select
          value={roleFilter === "" ? "all" : roleFilter}
          onValueChange={(value) => setRoleFilter(value === "all" ? "" : value)}
        >
          <SelectTrigger size="sm" active={roleFilter !== ""} className="h-8 w-40" aria-label="Role" data-testid="filter-role">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="all">all roles</SelectItem>
            {roles.map((role) => (
              <SelectItem key={role.name} value={role.name}>{role.name}</SelectItem>
            ))}
          </SelectContent>
        </Select>
        <Select
          value={providerFilter === "" ? "all" : providerFilter}
          onValueChange={(value) => setProviderFilter(value === "all" ? "" : value)}
        >
          <SelectTrigger size="sm" active={providerFilter !== ""} className="h-8 w-36" aria-label="Provider" data-testid="filter-provider">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="all">all providers</SelectItem>
            <SelectItem value="local">local</SelectItem>
            <SelectItem value="entra">entra</SelectItem>
          </SelectContent>
        </Select>
        <Select
          value={activeFilter === "" ? "all" : activeFilter}
          onValueChange={(value) => setActiveFilter(value === "all" ? "" : value)}
        >
          <SelectTrigger size="sm" active={activeFilter !== ""} className="h-8 w-36" aria-label="Active" data-testid="filter-active">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="all">all states</SelectItem>
            <SelectItem value="active">active</SelectItem>
            <SelectItem value="inactive">inactive</SelectItem>
          </SelectContent>
        </Select>
      </FilterBar>

      <PagedTable
        queryKey={["users", "list", usernameFilter, roleFilter, providerFilter, activeFilter]}
        fetchPage={(page, pageSize) => userApi.list({
          page,
          pageSize,
          username: usernameFilter === "" ? undefined : usernameFilter,
          role: roleFilter === "" ? undefined : roleFilter,
          provider: providerFilter === "" ? undefined : providerFilter,
          active: activeFilter === "" ? undefined : activeFilter === "active",
        })}
        columns={columns}
        rowKey={(row) => row.id}
        emptyMessage="No users match the current filters."
      />

      {editUser !== null && (
        <EditUserSheet user={editUser} onClose={() => setEditUser(null)} />
      )}
      {roleDialogUser !== null && (
        <ChangeRoleDialog user={roleDialogUser} roles={roles} onClose={() => setRoleDialogUser(null)} />
      )}
      {passwordDialogUser !== null && (
        <ResetPasswordDialog user={passwordDialogUser} onClose={() => setPasswordDialogUser(null)} />
      )}
      {createOpen && <CreateUserSheet roles={roles} onClose={() => setCreateOpen(false)} />}

      <ConfirmDialog
        open={deactivateUser !== null}
        title="Deactivate user"
        message={`Deactivate ${deactivateUser?.username ?? ""}? They will no longer be able to sign in.`}
        confirmLabel="Deactivate"
        danger
        busy={deactivate.isPending}
        onConfirm={() => {
          if (deactivateUser !== null) {
            deactivate.mutate(deactivateUser);
          }
        }}
        onClose={() => setDeactivateUser(null)}
      />

      <ConfirmDialog
        open={deleteUser !== null}
        title="Delete user"
        message={`Permanently delete ${deleteUser?.username ?? ""}? Their access tokens, notification subscriptions, and chat conversations go with the account. Run history keeps naming them. Deactivate instead to keep the account recoverable.`}
        confirmLabel="Delete"
        danger
        busy={remove.isPending}
        onConfirm={() => {
          if (deleteUser !== null) {
            remove.mutate(deleteUser);
          }
        }}
        onClose={() => setDeleteUser(null)}
      />
    </Page>
  );
}
