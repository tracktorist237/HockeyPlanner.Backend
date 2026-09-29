# HTTP consumer contracts (HP-72)

`ConsumerContractTests` uses the actual WebApplicationFactory pipeline and
PostgreSQL, not a parallel serializer. Its fixed synthetic seed never contains
real accounts. Only the generated correlation ID is replaced; all DTO keys,
nulls, numeric enums, dates, arrays and extension values remain unchanged.
Transfer 409 uses a fault-injected application service behind the real controller.

Normal tests compare live HTTP JSON against `Contracts/api-contract.json`.
Frontend API tests consume an exact copy. Both CI gates compare the repositories'
copies, using a same-named companion task branch when present, otherwise develop.
A mismatch fails closed. Neither side silently downloads a replacement fixture.

Intentional changes require a coordinated PR pair:

1. PowerShell: set `$env:HP_UPDATE_CONTRACT='1'` and
   `$env:HP_CONTRACT_OUTPUT='<absolute path>/api-contract.json'`.
2. Run `dotnet test --filter FullyQualifiedName~ConsumerContractTests`.
3. Unset both variables. Review the generated diff, including nested fields.
4. Copy the generated file to frontend
   `src/api/__fixtures__/api-contract.generated.json`; update affected consumers.
5. Run normal backend tests, frontend contract/component tests and both CI gates.

Never set HP_UPDATE_CONTRACT in CI. Existing PascalCase fixtures remain only as
explicit backward-compatibility tests. The generated fixture is the current wire
contract. The Python drift regression deliberately changes nested casing and
must reject it even though legacy attendance parsing tolerates PascalCase.

Initial rollout requires both task branches to exist before PR checks run.
Merge the reviewed pair together; staging fails closed if only one side of a
contract update has merged. Rerun the blocked gate after the companion merge.
