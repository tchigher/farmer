module WebApp

open Expecto
open Farmer
open Farmer.Builders
open Farmer.WebApp
open Farmer.Arm
open System
open Farmer.CoreTypes
open Microsoft.Azure.Management.WebSites
open Microsoft.Azure.Management.WebSites.Models
open Microsoft.Rest

let getResource<'T when 'T :> IArmResource> (data:IArmResource list) = data |> List.choose(function :? 'T as x -> Some x | _ -> None)
/// Client instance needed to get the serializer settings.
let dummyClient = new WebSiteManagementClient (Uri "http://management.azure.com", TokenCredentials "NotNullOrWhiteSpace")
let getResourceAtIndex o = o |> getResourceAtIndex dummyClient.SerializationSettings

let tests = testList "Web App Tests" [
    let getResources (wa:WebAppConfig) = (wa :> IBuilder).BuildResources Location.WestEurope
    test "Basic Web App has service plan and AI dependencies set" {
        let resources = webApp { name "test" } |> getResources
        let wa = resources |> getResource<Web.Site> |> List.head

        Expect.containsAll wa.Dependencies [ ResourceName "test-ai"; ResourceName "test-farm" ] "Missing dependencies"
        Expect.hasLength (resources |> getResource<Insights.Components>) 1 "Should be one AI component"
        Expect.hasLength (resources |> getResource<Web.ServerFarm>) 1 "Should be one server farm"
    }
    test "Web App allows renaming of service plan and AI" {
        let resources = webApp { name "test"; service_plan_name "supersp"; app_insights_name "superai" } |> getResources
        let wa = resources |> getResource<Web.Site> |> List.head

        Expect.containsAll wa.Dependencies [ ResourceName "supersp"; ResourceName "superai" ] "Missing dependencies"
        Expect.hasLength (resources |> getResource<Insights.Components>) 1 "Should be one AI component"
        Expect.hasLength (resources |> getResource<Web.ServerFarm>) 1 "Should be one server farm"
    }
    test "Web App creates dependencies but no resources with linked AI and Server Farm configs" {
        let sp = servicePlan { name "plan" }
        let ai = appInsights { name "ai" }
        let resources = webApp { name "test"; link_to_app_insights ai; link_to_service_plan sp } |> getResources
        let wa = resources |> getResource<Web.Site> |> List.head
        Expect.containsAll wa.Dependencies [ ResourceName "plan"; ResourceName "ai" ] "Missing dependencies"
        Expect.isEmpty (resources |> getResource<Insights.Components>) "Should be no AI component"
        Expect.isEmpty (resources |> getResource<Web.ServerFarm>) "Should be no server farm"
    }
    test "Web App does not create dependencies for unmanaged linked resources" {
        let resources = webApp { name "test"; link_to_unmanaged_app_insights "test"; link_to_unmanaged_service_plan "test2" } |> getResources
        let wa = resources |> getResource<Web.Site> |> List.head
        Expect.isEmpty wa.Dependencies "Should be no dependencies"
        Expect.isEmpty (resources |> getResource<Insights.Components>) "Should be no AI component"
        Expect.isEmpty (resources |> getResource<Web.ServerFarm>) "Should be no server farm"
    }
    test "Web app supports adding tags to resource" {
        let resources = webApp { name "test"; add_tag "key" "value"; add_tags ["alpha","a"; "beta","b"]} |> getResources
        let wa = resources |> getResource<Web.Site> |> List.head
        Expect.containsAll (wa.Tags|> Map.toSeq)
            [ "key","value"
              "alpha","a"
              "beta","b"]
            "Should contain the given tags"
        Expect.equal 3 (wa.Tags|> Map.count) "Should not contain additional tags"
    }
    test "Web App correctly adds connection strings" {
        let sa = storageAccount { name "foo" }
        let wa =
            let resources = webApp { name "test"; connection_string "a"; connection_string ("b", sa.Key) } |> getResources
            resources |> getResource<Web.Site> |> List.head

        let expected = [
            "a", (ParameterSetting(SecureParameter "a"), Custom)
            "b", (ExpressionSetting sa.Key, Custom)
        ]
        let parameters = wa :> IParameters

        Expect.equal wa.ConnectionStrings (Map expected) "Missing connections"
        Expect.equal parameters.SecureParameters [ SecureParameter "a" ] "Missing parameter"
    }
    test "CORS works correctly" {
        let wa : Site =
            webApp {
                name "test"
                enable_cors [ "https://bbc.co.uk" ]
                enable_cors_credentials
            }
            |> getResourceAtIndex 0
        Expect.sequenceEqual wa.SiteConfig.Cors.AllowedOrigins [ "https://bbc.co.uk" ] "Allowed Origins should be *"
        Expect.equal wa.SiteConfig.Cors.SupportCredentials (Nullable true) "Support Credentials"
    }

    test "If CORS is AllOrigins, cannot enable credentials" {
        Expect.throws (fun () ->
            webApp {
                name "test"
                enable_cors AllOrigins
                enable_cors_credentials
            } |> ignore) "Invalid CORS combination"
    }

    test "Automatically converts from * to AllOrigins" {
        let wa : Site =
            webApp { name "test"; enable_cors [ "*" ] } |> getResourceAtIndex 0
        Expect.sequenceEqual wa.SiteConfig.Cors.AllowedOrigins [ "*" ] "Allowed Origins should be *"
    }

    test "CORS without credentials does not crash" {
        webApp { name "test"; enable_cors AllOrigins } |> ignore
        webApp { name "test"; enable_cors [ "https://bbc.co.uk" ] } |> ignore
    }

    test "If CORS is not enabled, ignores enable credentials" {
        let wa : Site =
            webApp { name "test"; enable_cors_credentials } |> getResourceAtIndex 0
        Expect.isNull wa.SiteConfig.Cors "Should be no CORS settings"
    }

    test "Implicitly adds a dependency when adding a setting" {
        let sa = storageAccount { name "teststorage" }
        let sql = sqlServer { name "test"; admin_username "user"; add_databases [ sqlDb { name "thedb" } ] }
        let wa = webApp {
            name "testweb"
            setting "storage" sa.Key
            setting "conn" (sql.ConnectionString "thedb")
            setting "bad" (literal "ignore_me")
        }
        let wa = wa |> getResources |> getResource<Web.Site> |> List.head

        Expect.contains wa.Dependencies sa.Name.ResourceName "Storage Account is missing"
        Expect.contains wa.Dependencies (ResourceName "thedb") "Database is missing"
    }

    test "Implicitly adds a dependency when adding a connection string" {
        let sa = storageAccount { name "teststorage" }
        let wa = webApp { name "testweb"; setting "storage" sa.Key }
        let wa = wa |> getResources |> getResource<Web.Site> |> List.head
        Expect.contains wa.Dependencies sa.Name.ResourceName "Storage Account is missing"
    }
]