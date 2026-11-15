#!/usr/bin/env python3
"""Provisions a fresh Zitadel for ArtFlow through its management API (PAT of the first instance's machine user):
project "artflow" with the app's roles, machine users with client secrets and role grants, JWT access tokens.
Prints the project id and one client per line: name client_id client_secret user_id.
usage: setup.py <base url> <pat file> [<file for the project id (the API's audience)>]"""
import json, sys, time, urllib.error, urllib.request

import os
base = sys.argv[1].rstrip("/")
for _ in range(180):                               # under the AppHost: the first instance writes the PAT once initialised
    if os.path.exists(sys.argv[2]) and os.path.getsize(sys.argv[2]) > 0:
        break
    time.sleep(1)
pat = open(sys.argv[2]).read().strip()

def call(method, path, body=None):
    req = urllib.request.Request(base + path, method=method, data=json.dumps(body or {}).encode(),
                                 headers={"Authorization": "Bearer " + pat, "Content-Type": "application/json"})
    for attempt in range(120):                     # the first instance is still being set up: 503 for some seconds
        try:
            with urllib.request.urlopen(req) as r:
                return json.load(r)
        except urllib.error.HTTPError as e:
            if e.code != 503 or attempt == 119:
                raise
            time.sleep(1)
        except urllib.error.URLError:
            if attempt == 119:
                raise
            time.sleep(1)

project = call("POST", "/management/v1/projects", {"name": "artflow", "projectRoleAssertion": True})["id"]
call("POST", f"/management/v1/projects/{project}/roles/_bulk",
     {"roles": [{"key": k, "displayName": k} for k in ["seller", "bidder", "reader", "admin", "dealer", "service"]]})
print("project", project)
clients = []
for name, roles in [("alice", ["seller", "bidder"]), ("bob", ["bidder", "seller"]), ("rita", ["reader"]), ("nora", []), ("billing", ["service"])]:
    user = call("POST", "/management/v1/users/machine", {"userName": name, "name": name, "accessTokenType": "ACCESS_TOKEN_TYPE_JWT"})["userId"]
    secret = call("PUT", f"/management/v1/users/{user}/secret")
    if roles:
        call("POST", f"/management/v1/users/{user}/grants", {"projectId": project, "roleKeys": roles})
    clients.append(f"client {name} {secret['clientId']} {secret['clientSecret']} {user}")
    print(clients[-1])
if len(sys.argv) > 3:                              # files for the AppHost's api/worker (audience) and the driving script
    open(os.path.join(os.path.dirname(sys.argv[3]) or ".", "clients.txt"), "w").write("project " + project + "\n" + "\n".join(clients) + "\n")
    open(sys.argv[3], "w").write(project)          # written last: the api waits for the setup to complete
