using System;
using DennokoWorks.InspectorManagerV2.Application;
using UnityEditor;

namespace DennokoWorks.InspectorManagerV2.UnityAdapter
{
    /// <summary>
    /// Stores within-Editor-session layout (FixedDisplayNumber, RoleOrder, Exclusions, ManualUnlock)
    /// and a stable EditorSessionId via UnityEditor.SessionState (Section 9).
    /// Survives Assembly Reloads, clears automatically on Editor restart.
    /// </summary>
    public sealed class UnitySessionStateStore : ISessionLayoutStore
    {
        private const string EditorSessionIdKey = "DennokoWorks.InspectorManagerV2.EditorSessionId";
        private const string SessionLayoutJsonKey = "DennokoWorks.InspectorManagerV2.SessionLayoutJson";

        public string GetEditorSessionId()
        {
            string id = SessionState.GetString(EditorSessionIdKey, string.Empty);
            if (string.IsNullOrEmpty(id))
            {
                id = Guid.NewGuid().ToString("N");
                SessionState.SetString(EditorSessionIdKey, id);
            }
            return id;
        }

        public SessionLayoutDto LoadSessionLayout()
        {
            string raw = SessionState.GetString(SessionLayoutJsonKey, string.Empty);
            if (string.IsNullOrEmpty(raw))
            {
                return null;
            }

            try
            {
                var dict = SimpleJson.DeserializeObject(raw);
                return SessionLayoutDto.FromJsonDictionary(dict);
            }
            catch
            {
                return null;
            }
        }

        public void SaveSessionLayout(SessionLayoutDto dto)
        {
            if (dto == null) return;
            try
            {
                string json = SimpleJson.Serialize(dto.ToJsonDictionary());
                SessionState.SetString(SessionLayoutJsonKey, json);
            }
            catch
            {
                // Ignore SessionState serialization errors.
            }
        }

        public void ClearSessionLayout()
        {
            SessionState.EraseString(SessionLayoutJsonKey);
        }
    }
}
