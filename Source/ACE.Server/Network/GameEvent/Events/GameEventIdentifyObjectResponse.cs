using System.IO;

using ACE.Entity.Enum;
using ACE.Server.Network.Structure;
using ACE.Server.WorldObjects;

namespace ACE.Server.Network.GameEvent.Events
{
    public class GameEventIdentifyObjectResponse : GameEventMessage
    {
        /// <param name="answerGuid">
        /// The guid the response is filed under, or null for <paramref name="obj"/>'s own. The client files
        /// an appraisal under the guid it carries, so it must be the guid the client ASKED about - which
        /// differs from obj's own only for a mule panel row's display alias (see Player.AppraisalAnswerGuid).
        /// </param>
        public GameEventIdentifyObjectResponse(Session session, WorldObject obj, bool success, uint? answerGuid = null)
            : base(GameEventType.IdentifyObjectResponse, GameMessageGroup.UIQueue, session)
        {
            var appraiseInfo = new AppraiseInfo(obj, session.Player, success);

            WriteBody(Writer, obj, appraiseInfo, answerGuid);
        }

        /// <summary>
        /// The response body: the guid it is filed under, then the appraisal. Static and Session-free so a
        /// test can read exactly what goes on the wire for a given answer guid.
        /// </summary>
        internal static void WriteBody(BinaryWriter writer, WorldObject obj, AppraiseInfo appraiseInfo, uint? answerGuid)
        {
            writer.Write(answerGuid ?? obj.Guid.Full);
            writer.Write(appraiseInfo);
        }

        // Empty Appraisal response, for when you only have a guid and nothing else.
        public GameEventIdentifyObjectResponse(Session session, uint objectGuid)
            : base(GameEventType.IdentifyObjectResponse, GameMessageGroup.UIQueue, session)
        {
            var appraiseInfo = new AppraiseInfo();

            Writer.Write(objectGuid);
            Writer.Write(appraiseInfo);
        }
    }
}
