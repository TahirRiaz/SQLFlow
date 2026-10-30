import { useState, type FormEvent } from "react";
import { useSearchParams } from "react-router-dom";
import { useMutation } from "@tanstack/react-query";
import { CircleCheck, CircleX, Loader2 } from "lucide-react";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { isApiError } from "../api/client";
import { authApi } from "../api/endpoints";
import { CorrelationError } from "../components/CorrelationError";
import { branding } from "../modules/branding";
import { useAuth } from "./AuthContext";

/**
 * Where a person finishes a client's browser sign-in: an editor, an MCP server or anything else that signs in with the
 * device grant sends them here with its code. The page sits behind the GUI's sign-in, so a person who signs in with
 * Microsoft reaches it with that sign-in, which the control plane's own /device page cannot offer. Approving always
 * takes a click on a code the person can compare with the client's: a link that approved by itself would let anyone
 * who sends one sign in as whoever opens it.
 */
export default function DeviceApprovalPage() {
  const [params] = useSearchParams();
  const { session } = useAuth();
  const brand = branding();
  const [code, setCode] = useState(params.get("code")?.trim() ?? "");

  const approve = useMutation({ mutationFn: (userCode: string) => authApi.approveDevice(userCode) });
  const deny = useMutation({ mutationFn: (userCode: string) => authApi.denyDevice(userCode) });
  const answered = approve.isSuccess || deny.isSuccess;
  const pending = approve.isPending || deny.isPending;
  const failure = approve.error ?? deny.error;

  function submit(event: FormEvent) {
    event.preventDefault();
    if (code.trim() !== "") {
      approve.mutate(code.trim());
    }
  }

  return (
    <div className="flex min-h-0 flex-1 items-center justify-center p-4">
      <div className="w-full max-w-md space-y-5 rounded-lg border bg-card p-6">
        <div>
          <h1 className="text-lg font-semibold">{`Sign in to ${brand.productName}`}</h1>
          <p className="mt-1 text-sm text-muted-foreground">
            {`A client is asking to act as ${session?.subject ?? "you"}: reading the estate and starting runs, with no more access than your account has.`}
          </p>
        </div>

        {approve.isSuccess && (
          <Alert>
            <CircleCheck className="size-4" />
            <AlertTitle>Signed in</AlertTitle>
            <AlertDescription>The client is signed in. You can close this tab and go back to it.</AlertDescription>
          </Alert>
        )}

        {deny.isSuccess && (
          <Alert>
            <CircleX className="size-4" />
            <AlertTitle>Sign-in denied</AlertTitle>
            <AlertDescription>The client stays signed out. You can close this tab.</AlertDescription>
          </Alert>
        )}

        {!answered && (
          <form className="space-y-4" onSubmit={submit}>
            <div className="space-y-1.5">
              <Label htmlFor="device-code">Code</Label>
              <Input
                id="device-code"
                value={code}
                onChange={(event) => setCode(event.target.value)}
                autoComplete="off"
                spellCheck={false}
                className="font-mono tracking-widest"
              />
              <p className="text-xs text-muted-foreground">Approve only if this is the code your client shows.</p>
            </div>

            {isApiError(failure) && <CorrelationError error={failure} />}

            <div className="flex gap-2">
              <Button type="submit" disabled={pending || code.trim() === ""}>
                {approve.isPending && <Loader2 className="size-4 animate-spin" />}
                Approve
              </Button>
              <Button
                type="button"
                variant="outline"
                disabled={pending || code.trim() === ""}
                onClick={() => deny.mutate(code.trim())}
              >
                {deny.isPending && <Loader2 className="size-4 animate-spin" />}
                Deny
              </Button>
            </div>
          </form>
        )}
      </div>
    </div>
  );
}
